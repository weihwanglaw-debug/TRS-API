using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using Stripe;
using Stripe.Checkout;
using TRS_API.Models;
using TRS_API.Services;
using TRS_Data.Models;

namespace TRS_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // NO [Authorize] - public access for event registration payments
    public class PaymentController : ControllerBase
    {
        private readonly ILogger<PaymentController> _logger;
        private readonly IConfiguration _config;
        private readonly TRSDbContext _db;
        private readonly IBackgroundJobQueue _jobQueue;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly RegistrationWorkflowService _registrationWorkflow;
        private readonly PaymentFinalizationService _paymentFinalization;

        public PaymentController(
            ILogger<PaymentController> logger,
            IConfiguration config,
            TRSDbContext db,
            IBackgroundJobQueue jobQueue,
            IServiceScopeFactory serviceScopeFactory,
            RegistrationWorkflowService registrationWorkflow,
            PaymentFinalizationService paymentFinalization)
        {
            _logger = logger;
            _config = config;
            _db = db;
            _jobQueue = jobQueue;
            _serviceScopeFactory = serviceScopeFactory;
            _registrationWorkflow = registrationWorkflow;
            _paymentFinalization = paymentFinalization;
            StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        }

        // -- GET /api/Payment/get-payment-info/:registrationId -----------------
        [HttpGet("get-payment-info/{registrationId}")]
        [EnableRateLimiting("payment")]
        public async Task<IActionResult> GetPaymentInfo(int registrationId)
        {
            try
            {
                var registration = await _db.EventRegistrations
                    .FirstOrDefaultAsync(r => r.RegistrationId == registrationId);

                if (registration == null)
                    return NotFound(new { message = "Registration not found" });

                var existingPayment = await _db.Payments
                    .Where(p => p.RegistrationId == registrationId && p.PaymentStatus == "S")
                    .FirstOrDefaultAsync();

                return Ok(new
                {
                    registrationId     = registration.RegistrationId,
                    amount             = registration.TotalAmount,
                    currency           = registration.Currency,
                    registrationStatus = registration.RegistrationStatus,
                    isPaid             = existingPayment != null,
                    message            = existingPayment != null ? "Already paid" : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching payment info for registration {RegId}", registrationId);
                return StatusCode(500, new { message = "Failed to load payment information" });
            }
        }

        // -- POST /api/Payment/create-checkout-session -------------------------
        [EnableRateLimiting("payment")]
        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] PaymentRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Invalid request" });

            try
            {
                if (request.IsSessionFirst)
                    return await CreateSessionFirstCheckout(request);

                if (request.RegistrationId <= 0)
                    return BadRequest(new { message = "Invalid registration ID" });

                return await CreateLegacyCheckout(request);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe error creating checkout session");
                var message = ex.StripeError?.Code switch
                {
                    "payment_method_not_available" =>
                        "PayNow is not enabled on this Stripe account. Please use Credit Card.",
                    "amount_too_small" => "Minimum payment amount is SGD 0.50.",
                    _ => "Payment gateway error. Please try again."
                };
                return StatusCode(500, new { message, code = ex.StripeError?.Code });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating checkout session");
                return StatusCode(500, new { message = "Failed to create payment session" });
            }
        }

        private async Task<IActionResult> CreateSessionFirstCheckout(PaymentRequest request)
        {
            var payload = JsonSerializer.Deserialize<CreateRegistrationRequest>(
                request.RegistrationPayload!.Value.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null)
                return BadRequest(new { message = "Invalid registration payload" });

            var pricing = await _registrationWorkflow.ValidateAndPriceAsync(payload, new RegistrationValidationOptions
            {
                RequireEventOpen = true,
                ValidatePricingAgainstCurrentPrograms = true,
            });
            if (!pricing.Success)
                return BadRequest(new { code = pricing.Code, message = pricing.Message });

            var totalAmount = pricing.Value!.TotalAmount;
            if (totalAmount <= 0)
                return BadRequest(new { message = "Total amount must be greater than zero" });
            var expectedAmountCents = ToMinorUnits(totalAmount);
            var newPayloadJson = request.RegistrationPayload!.Value.GetRawText();
            var newPayloadHash = ComputePayloadHash(newPayloadJson);

            var currency    = pricing.Value.Currency;
            var method      = (request.PaymentMethod ?? "card").ToLower().Trim();
            var isPayNow    = method == "paynow";
            var stripeMethod = isPayNow ? "paynow" : "card";
            var dbMethod    = isPayNow ? "PayNow" : "CreditCard";

            if (isPayNow && !currency.Equals("SGD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "PayNow is only available for SGD payments." });

            var options = new SessionCreateOptions
            {
                Mode               = "payment",
                PaymentMethodTypes = new List<string> { stripeMethod },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency   = currency.ToLower(),
                            UnitAmount = (long)(totalAmount * 100),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name        = "Tournament Registration",
                                Description = payload.EventName
                            }
                        },
                        Quantity = 1
                    }
                },
                SuccessUrl = request.SuccessUrl ?? $"{Request.Scheme}://{Request.Host}/payment/result?status=success",
                CancelUrl  = request.CancelUrl  ?? $"{Request.Scheme}://{Request.Host}/payment/result?status=cancel",

                // ── CHANGED: added contact_name and contact_phone to metadata ──
                // These three fields are stored on WebhookLog at receipt time so that
                // Case-C admin reconciliation rows always show payer contact info
                // without requiring a live Stripe API call per row.
                Metadata = new Dictionary<string, string>
                {
                    { "flow",           "session_first" },
                    { "payment_method", dbMethod        },
                    { "event_id",       payload.EventId.ToString() },
                    { "contact_email",  payload.ContactEmail ?? "" },
                    { "contact_name",   payload.ContactName  ?? "" },   // ADDED
                    { "contact_phone",  payload.ContactPhone ?? "" },   // ADDED
                },

                // ── CHANGED: collect phone number at Stripe checkout ──────────
                // Safety net in case metadata is missing for any reason.
                // session.CustomerDetails.Phone will be populated for all future sessions.
                PhoneNumberCollection = new SessionPhoneNumberCollectionOptions   // ADDED
                {
                    Enabled = true,
                },
            };

            if (isPayNow) options.ExpiresAt = DateTime.UtcNow.AddMinutes(30);

            // ── ONE ACTIVE PAYMENT LOCK RULE ──────────────────────────────────
            var existingActive = await _db.PendingCheckouts
                .Where(p => p.EventId        == payload.EventId
                        && p.ContactEmail    == (payload.ContactEmail ?? "")
                        && p.PaymentMethod   == dbMethod
                        && p.ExpiresAt       > DateTime.UtcNow)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingActive != null)
            {
                Session? existingSession = null;
                try
                {
                    existingSession = await new SessionService().GetAsync(existingActive.GatewaySessionId);
                }
                catch (StripeException ex)
                {
                    _logger.LogWarning(ex,
                        "Existing PendingCheckout session {SessionId} not found on Stripe; creating new session",
                        existingActive.GatewaySessionId);
                    existingSession = null;
                }

                if (existingSession != null && existingSession.Status == "open")
                {
                    var existingPayloadHash = ComputePayloadHash(existingActive.PayloadJson);
                    var existingAmountCents = existingSession.AmountTotal ?? 0;
                    if (existingAmountCents == expectedAmountCents &&
                        string.Equals(existingPayloadHash, newPayloadHash, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "Reusing existing active PendingCheckout session {SessionId} for event {EventId} contact {Email}",
                            existingActive.GatewaySessionId, payload.EventId, payload.ContactEmail);

                        existingActive.PaymentMethod = dbMethod;
                        existingActive.ExpiresAt = existingSession.ExpiresAt;
                        await _db.SaveChangesAsync();

                        return Ok(new
                        {
                            checkoutUrl      = existingSession.Url,
                            gatewaySessionId = existingActive.GatewaySessionId,
                            paymentMethod    = dbMethod,
                            expiresAt        = existingActive.ExpiresAt
                        });
                    }

                    _logger.LogInformation(
                        "Expiring stale PendingCheckout session {SessionId}: amount/hash mismatch for event {EventId} contact {Email}",
                        existingActive.GatewaySessionId, payload.EventId, payload.ContactEmail);
                    try
                    {
                        await new SessionService().ExpireAsync(existingActive.GatewaySessionId);
                    }
                    catch (StripeException ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to expire stale Stripe session {SessionId}; removing local PendingCheckout row",
                            existingActive.GatewaySessionId);
                    }
                }

                _db.PendingCheckouts.Remove(existingActive);
                await _db.SaveChangesAsync();
            }

            var idempotencyKey = $"sf_{payload.EventId}_{method}_{payload.ContactEmail}_{(int)(totalAmount * 100)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600}";
            var requestOptions = new RequestOptions { IdempotencyKey = idempotencyKey };

            var session = await new SessionService().CreateAsync(options, requestOptions);

            _logger.LogInformation(
                "Created session-first {Method} Stripe session {SessionId} for event {EventId} contact {Email}",
                dbMethod, session.Id, payload.EventId, payload.ContactEmail);

            var newExpiresAt   = session.ExpiresAt;

            var existing = await _db.PendingCheckouts.FindAsync(session.Id);

            if (existing == null)
            {
                _db.PendingCheckouts.Add(new TRS_Data.Models.PendingCheckout
                {
                    GatewaySessionId = session.Id,
                    EventId          = payload.EventId,
                    ContactEmail     = payload.ContactEmail ?? "",
                    PayloadJson      = newPayloadJson,
                    PaymentMethod    = dbMethod,
                    CreatedAt        = DateTime.UtcNow,
                    ExpiresAt        = newExpiresAt,
                });
            }
            else
            {
                existing.PayloadJson   = newPayloadJson;
                existing.ContactEmail  = payload.ContactEmail ?? "";
                existing.PaymentMethod = dbMethod;
                existing.ExpiresAt     = newExpiresAt;
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "PendingCheckout {Action} for session {SessionId} event {EventId}",
                existing == null ? "created" : "updated", session.Id, payload.EventId);

            return Ok(new
            {
                checkoutUrl      = session.Url,
                gatewaySessionId = session.Id,
                paymentMethod    = dbMethod,
                expiresAt        = session.ExpiresAt
            });
        }

        private async Task<IActionResult> CreateLegacyCheckout(PaymentRequest request)
        {
            var registration = await _db.EventRegistrations
                .FirstOrDefaultAsync(r => r.RegistrationId == request.RegistrationId);

            if (registration == null)
                return NotFound(new { message = "Registration not found" });

            if (registration.RegistrationStatus == "C")
                return BadRequest(new { message = "Already confirmed/paid" });

            if (registration.RegistrationStatus == "X")
                return BadRequest(new { message = "Cancelled" });

            var existingPayment = await _db.Payments
                .Where(p => p.RegistrationId == request.RegistrationId && p.PaymentStatus == "S")
                .FirstOrDefaultAsync();

            if (existingPayment != null)
                return BadRequest(new { message = "Payment already completed" });

            var method      = (request.PaymentMethod ?? "card").ToLower().Trim();
            var isPayNow    = method == "paynow";
            var stripeMethod = isPayNow ? "paynow" : "card";
            var dbMethod    = isPayNow ? "PayNow" : "CreditCard";

            if (isPayNow && !registration.Currency.Equals("SGD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "PayNow is only available for SGD payments." });

            var options = new SessionCreateOptions
            {
                Mode               = "payment",
                PaymentMethodTypes = new List<string> { stripeMethod },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency   = registration.Currency.ToLower(),
                            UnitAmount = (long)(registration.TotalAmount * 100),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name        = "Tournament Registration",
                                Description = $"Registration #{registration.RegistrationId} - {registration.EventName}"
                            }
                        },
                        Quantity = 1
                    }
                },
                SuccessUrl = request.SuccessUrl ??
                    $"{Request.Scheme}://{Request.Host}/payment/result?reg={registration.RegistrationId}",
                CancelUrl = request.CancelUrl ??
                    $"{Request.Scheme}://{Request.Host}/payment/result?status=cancel&reg={registration.RegistrationId}",
                ClientReferenceId = registration.RegistrationId.ToString(),
                Metadata = new Dictionary<string, string>
                {
                    { "flow",            "legacy" },
                    { "registration_id", registration.RegistrationId.ToString() },
                    { "payment_method",  dbMethod }
                }
            };

            if (isPayNow) options.ExpiresAt = DateTime.UtcNow.AddMinutes(30);

            var requestOptions = new RequestOptions
            {
                IdempotencyKey = $"checkout_{method}_reg_{registration.RegistrationId}"
            };

            var session = await new SessionService().CreateAsync(options, requestOptions);

            _logger.LogInformation(
                "Created legacy {Method} Stripe session {SessionId} for registration {RegId}",
                dbMethod, session.Id, registration.RegistrationId);

            return Ok(new
            {
                checkoutUrl      = session.Url,
                gatewaySessionId = session.Id,
                paymentMethod    = dbMethod,
                expiresAt        = session.ExpiresAt
            });
        }

        // -- POST /api/Payment/confirm-session ---------------------------------
        [EnableRateLimiting("payment")]
        [HttpPost("confirm-session")]
        public async Task<IActionResult> ConfirmSession([FromBody] ConfirmSessionRequest request)
        {
            StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];

            var verifiedSessionService = new SessionService();
            Session verifiedSession;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                verifiedSession = await verifiedSessionService.GetAsync(
                    request.GatewaySessionId,
                    cancellationToken: cts.Token);
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe session not found: {SessionId}", request.GatewaySessionId);
                return BadRequest(new { message = "Payment session not found. Please contact the organiser." });
            }

            if (verifiedSession.PaymentStatus != "paid")
            {
                _logger.LogWarning("Session {SessionId} not paid - status: {Status}", request.GatewaySessionId, verifiedSession.PaymentStatus);
                return BadRequest(new { message = "Payment has not been confirmed by Stripe." });
            }

            var result = await _paymentFinalization.FinalizeSessionFirstAsync(verifiedSession);
            if (!result.Success)
            {
                await UpsertConfirmSessionFailureLogAsync(
                    verifiedSession,
                    $"{result.Code}: {result.Message}");

                if (string.Equals(result.Code, "CHECKOUT_CONTEXT_MISSING", StringComparison.Ordinal))
                    return Conflict(new { code = result.Code, message = result.Message });

                var isNotFound = string.Equals(result.Code, "EVENT_NOT_FOUND", StringComparison.Ordinal)
                    || string.Equals(result.Code, "PROGRAM_NOT_FOUND", StringComparison.Ordinal);
                return isNotFound
                    ? NotFound(new { code = result.Code, message = result.Message })
                    : BadRequest(new { code = result.Code, message = result.Message });
            }

            return Ok(new { registrationId = result.RegistrationId.ToString() });
        }

        private async Task UpsertConfirmSessionFailureLogAsync(Session session, string errorMessage)
        {
            var existingPayment = await _db.Payments
                .AsNoTracking()
                .AnyAsync(p => p.GatewaySessionId == session.Id);
            if (existingPayment) return;

            var log = await _db.WebhookLogs
                .FirstOrDefaultAsync(w =>
                    w.GatewaySessionId == session.Id &&
                    w.ProcessingStatus == "F" &&
                    (w.EventType == "checkout.session.completed" || w.EventType == "processing_error"));

            var now = DateTime.UtcNow;
            if (log == null)
            {
                log = new WebhookLog
                {
                    PaymentGateway = "stripe",
                    GatewayEventId = $"confirm_session_{session.Id}",
                    EventType = "checkout.session.completed",
                    PayloadJson = JsonSerializer.Serialize(session),
                    ProcessingStatus = "F",
                    ReceivedAt = now,
                };
                _db.WebhookLogs.Add(log);
            }

            log.ErrorMessage = errorMessage;
            log.ProcessedAt = now;
            log.GatewaySessionId = session.Id;
            log.ContactName = session.Metadata?.GetValueOrDefault("contact_name")
                              ?? session.CustomerDetails?.Name;
            log.ContactEmail = session.Metadata?.GetValueOrDefault("contact_email")
                               ?? session.CustomerDetails?.Email;
            log.ContactPhone = session.Metadata?.GetValueOrDefault("contact_phone")
                               ?? session.CustomerDetails?.Phone;
            log.Amount = session.AmountTotal.HasValue
                ? session.AmountTotal.Value / 100m
                : null;
            log.Currency = session.Currency?.ToUpperInvariant();

            await _db.SaveChangesAsync();
        }

        // -- GET /api/Payment/verify/:paymentId --------------------------------
        [HttpGet("verify/{paymentId}")]
        public async Task<IActionResult> VerifyPayment(int paymentId)
        {
            try
            {
                var payment = await _db.Payments
                    .Include(p => p.Registration)
                    .FirstOrDefaultAsync(p => p.PaymentId == paymentId);

                if (payment == null)
                    return NotFound(new { message = "Payment not found" });

                return Ok(new
                {
                    paymentId        = payment.PaymentId,
                    registrationId   = payment.RegistrationId,
                    amount           = payment.Amount,
                    currency         = payment.Currency,
                    status           = payment.PaymentStatus,
                    method           = payment.PaymentMethod,
                    paidAt           = payment.PaidAt,
                    receiptNumber    = payment.ReceiptNumber,
                    gatewayPaymentId = payment.GatewayPaymentId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying payment {PaymentId}", paymentId);
                return StatusCode(500, new { message = "Failed to verify payment" });
            }
        }

        internal static void ApplyRefundOutcome(Payment payment)
        {
            var paidAmount = payment.Amount;
            var refundedAmount = payment.Refunds
                .Where(r => r.RefundStatus == "S")
                .Sum(r => r.RefundAmount);

            payment.PaymentStatus = refundedAmount switch
            {
                <= 0m => "S",
                var amount when amount >= paidAmount => "FR",
                _ => "PR",
            };
            payment.UpdatedAt = DateTime.UtcNow;
        }

        internal static void ApplyRefundItemOutcome(Payment payment, PaymentItem item)
        {
            var refundedAmount = payment.Refunds
                .Where(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "S")
                .Sum(r => r.RefundAmount);

            item.ItemStatus = refundedAmount >= item.Amount ? "R" : "S";
            item.UpdatedAt = DateTime.UtcNow;
        }

        private static long ToMinorUnits(decimal amount) =>
            decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));

        private static string ComputePayloadHash(string payloadJson)
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var normalized = JsonSerializer.Serialize(doc.RootElement);
            var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes);
        }
    }
}
