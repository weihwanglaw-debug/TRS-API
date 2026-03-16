using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TRS_API.Services;
using TRS_Data;
using TRS_Data.Models;

namespace TRS_API.Controllers
{
    [ApiController]
    [Route("api/webhooks/stripe")]
    [AllowAnonymous]
    public class StripeWebhookController : ControllerBase
    {
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly IConfiguration _config;
        private readonly TRSDbContext _db;
        private readonly IBackgroundJobQueue _jobQueue;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IConfiguration config,
            TRSDbContext db,
            IBackgroundJobQueue jobQueue,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _config = config;
            _db = db;
            _jobQueue = jobQueue;
            _serviceScopeFactory = serviceScopeFactory;
        }

        [HttpPost]
        public async Task<IActionResult> Webhook()
        {
            string json = string.Empty;
            string eventId = string.Empty;

            try
            {
                json = await new StreamReader(Request.Body).ReadToEndAsync();
                var stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _config["Stripe:WebhookSecret"]
                );

                eventId = stripeEvent.Id;

                // Handle events
                switch (stripeEvent.Type)
                {
                    case "checkout.session.completed":
                        var session = stripeEvent.Data.Object as Session;
                        await HandleCheckoutCompleted(session!, eventId);
                        break;

                    case "checkout.session.expired":
                        var expiredSession = stripeEvent.Data.Object as Session;
                        await HandleCheckoutExpired(expiredSession!, eventId);
                        break;

                    default:
                        // Log ignored events
                        _db.WebhookLogs.Add(new WebhookLog
                        {
                            PaymentGateway = "stripe",
                            GatewayEventId = eventId,
                            EventType = stripeEvent.Type,
                            PayloadJson = json,
                            ProcessingStatus = "I",
                            ReceivedAt = DateTime.UtcNow,
                            ProcessedAt = DateTime.UtcNow
                        });
                        await _db.SaveChangesAsync();
                        break;
                }

                return Ok();
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Webhook signature verification failed");

                _db.WebhookLogs.Add(new WebhookLog
                {
                    PaymentGateway = "stripe",
                    GatewayEventId = "unknown",
                    EventType = "signature_verification_failed",
                    PayloadJson = json,
                    ProcessingStatus = "F",
                    ErrorMessage = ex.Message,
                    ReceivedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();

                return BadRequest();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook processing error for event {EventId}", eventId);

                try
                {
                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway = "stripe",
                        GatewayEventId = eventId ?? "unknown",
                        EventType = "processing_error",
                        PayloadJson = json,
                        ProcessingStatus = "F",
                        ErrorMessage = ex.Message,
                        ReceivedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                }
                catch { /* ignore logging errors */ }

                return StatusCode(500);
            }
        }

        private async Task HandleCheckoutCompleted(Session session, string eventId)
        {
            // Check deduplication FIRST
            var existingLog = await _db.WebhookLogs
                .FirstOrDefaultAsync(e => e.GatewayEventId == eventId && e.ProcessingStatus == "S");

            if (existingLog != null)
            {
                _logger.LogInformation("Duplicate webhook ignored: {EventId}", eventId);
                return;
            }

            const int maxRetries = 3;
            int attempt = 0;

            while (attempt < maxRetries)
            {
                using var transaction = await _db.Database.BeginTransactionAsync();
                try
                {
                    // ✅ Get registration ID from session metadata
                    if (!session.Metadata.TryGetValue("registration_id", out var regIdStr) ||
                        !int.TryParse(regIdStr, out var registrationId))
                    {
                        _logger.LogWarning("Registration ID not found in session {SessionId}", session.Id);
                        await transaction.RollbackAsync();
                        return;
                    }

                    var registration = await _db.EventRegistrations
                        .FirstOrDefaultAsync(r => r.RegistrationId == registrationId);

                    if (registration == null)
                    {
                        _logger.LogWarning("Registration {RegId} not found", registrationId);
                        await transaction.RollbackAsync();
                        return;
                    }

                    // ✅ Check if payment already exists (deduplication)
                    var existingPayment = await _db.Payments
                        .FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id);

                    if (existingPayment != null)
                    {
                        _logger.LogInformation("Payment already processed for session {SessionId}", session.Id);
                        await transaction.RollbackAsync();
                        return;
                    }

                    // ✅ CREATE PAYMENT RECORD NOW (only after successful Stripe payment)
                    var payment = new Payment
                    {
                        RegistrationId = registrationId,
                        Amount = registration.TotalAmount,
                        Currency = registration.Currency,
                        PaymentGateway = "stripe",
                        PaymentMethod = "card",
                        PaymentStatus = "S", // ✅ Successful
                        GatewaySessionId = session.Id,
                        GatewayPaymentId = session.PaymentIntentId,
                        PaidAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow
                    };

                    _db.Payments.Add(payment);

                    // ✅ Update registration status
                    registration.RegistrationStatus = "C";

                    // Log webhook
                    var webhookLog = new WebhookLog
                    {
                        PaymentGateway = "stripe",
                        GatewayEventId = eventId,
                        EventType = "checkout.session.completed",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "S",
                        ReceivedAt = DateTime.UtcNow,
                        ProcessedAt = DateTime.UtcNow
                    };

                    _db.WebhookLogs.Add(webhookLog);

                    try
                    {
                        await _db.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate") == true)
                    {
                        _logger.LogInformation("Duplicate webhook detected during save: {EventId}", eventId);
                        await transaction.RollbackAsync();
                        return;
                    }

                    await transaction.CommitAsync();

                    // Queue background job for email/receipt
                    await _jobQueue.EnqueueAsync(async ct =>
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var emailSvc = scope.ServiceProvider.GetRequiredService<EmailService>();

                        try
                        {
                            // await emailSvc.SendPaymentConfirmationAsync(payment.PaymentId, ct);
                            _logger.LogInformation("Payment confirmation queued for {PaymentId}", payment.PaymentId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send payment confirmation for {PaymentId}", payment.PaymentId);
                        }
                    });

                    _logger.LogInformation("Successfully processed payment {PaymentId} for registration {RegId}",
                        payment.PaymentId, registrationId);

                    return; // Success - exit retry loop
                }
                catch (DbUpdateException ex) when (attempt < maxRetries - 1)
                {
                    await transaction.RollbackAsync();
                    attempt++;
                    _logger.LogWarning(ex, "Retry attempt {Attempt} for webhook {EventId}", attempt, eventId);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();

                    // Log failure
                    try
                    {
                        _db.WebhookLogs.Add(new WebhookLog
                        {
                            PaymentGateway = "stripe",
                            GatewayEventId = eventId,
                            EventType = "checkout.session.completed",
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(session),
                            ProcessingStatus = "F",
                            ErrorMessage = ex.Message,
                            ReceivedAt = DateTime.UtcNow,
                            ProcessedAt = DateTime.UtcNow
                        });
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "Failed to log webhook error for {EventId}", eventId);
                    }

                    throw;
                }
            }
        }


        private async Task HandleCheckoutExpired(Session session, string eventId)
        {
            try
            {
                var payment = await _db.Payments.FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id);

                if (payment != null && payment.PaymentStatus == "P")
                {
                    payment.PaymentStatus = "X"; // Cancelled/expired

                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway = "stripe",
                        GatewayEventId = eventId,
                        EventType = "checkout.session.expired",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "S",
                        ReceivedAt = DateTime.UtcNow,
                        ProcessedAt = DateTime.UtcNow
                    });

                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling checkout expired for session {SessionId}", session.Id);
            }
        }
    }
}
