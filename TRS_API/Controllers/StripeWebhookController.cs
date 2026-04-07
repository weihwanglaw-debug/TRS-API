using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TRS_API.Models;
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

                    case "charge.refunded":
                        var refundedCharge = stripeEvent.Data.Object as Charge;
                        await HandleChargeRefunded(refundedCharge!, eventId);
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
            // ── Session-first flow: recover via PendingCheckouts ledger ───────
            // Primary path: confirm-session() already wrote the registration and
            // purged the PendingCheckout row. The idempotency check below catches
            // that case and exits cleanly.
            //
            // Recovery path: user never returned to /payment/result (closed browser,
            // network drop, etc.). The PendingCheckout row still exists. We read the
            // stored payload JSON and perform the same DB write as confirm-session().
            if (session.Metadata.TryGetValue("flow", out var flow) && flow == "session_first")
            {
                // Idempotency: if confirm-session() already ran, a Payment row exists.
                var alreadyConfirmed = await _db.Payments
                    .AnyAsync(p => p.GatewaySessionId == session.Id && p.PaymentStatus == "S");

                if (alreadyConfirmed)
                {
                    _logger.LogInformation(
                        "Webhook: session-first session {SessionId} already confirmed by confirm-session — no action",
                        session.Id);
                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway   = "stripe",
                        GatewayEventId   = eventId,
                        EventType        = "checkout.session.completed",
                        PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "I",
                        ReceivedAt       = DateTime.UtcNow,
                        ProcessedAt      = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                    return;
                }

                // Look up the pending checkout ledger row.
                var pending = await _db.PendingCheckouts
                    .FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id);

                if (pending == null)
                {
                    _logger.LogWarning(
                        "Webhook: no PendingCheckout row found for session-first session {SessionId} — may already be processed",
                        session.Id);
                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway   = "stripe",
                        GatewayEventId   = eventId,
                        EventType        = "checkout.session.completed",
                        PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "I",
                        ReceivedAt       = DateTime.UtcNow,
                        ProcessedAt      = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                    return;
                }

                // Deserialize the stored payload and perform the DB write.
                _logger.LogInformation(
                    "Webhook: recovering session-first session {SessionId} from PendingCheckouts ledger",
                    session.Id);

                try
                {
                    await WriteSessionFirstRegistration(session, pending.PayloadJson, eventId);
                    // Purge the ledger row — registration is now in DB.
                    _db.PendingCheckouts.Remove(pending);
                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Webhook: failed to recover session-first session {SessionId}", session.Id);
                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway   = "stripe",
                        GatewayEventId   = eventId,
                        EventType        = "checkout.session.completed",
                        PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "F",
                        ErrorMessage     = ex.Message,
                        ReceivedAt       = DateTime.UtcNow,
                        ProcessedAt      = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                    throw;
                }
                return;
            }

            // ── Legacy flow: registration already in DB, update payment status ──
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

                    var existingPayment = await _db.Payments
                        .FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id);

                    if (existingPayment != null)
                    {
                        _logger.LogInformation("Payment already processed for session {SessionId}", session.Id);
                        await transaction.RollbackAsync();
                        return;
                    }

                    session.Metadata.TryGetValue("payment_method", out var paymentMethodMeta);
                    var paymentMethod = paymentMethodMeta ?? "CreditCard";

                    var payment = new Payment
                    {
                        RegistrationId   = registrationId,
                        EventId          = registration.EventId,
                        Amount           = registration.TotalAmount,
                        Currency         = registration.Currency,
                        PaymentGateway   = "Stripe",
                        PaymentMethod    = paymentMethod,
                        PaymentStatus    = "S",
                        GatewaySessionId = session.Id,
                        GatewayPaymentId = session.PaymentIntentId,
                        PaidAt           = DateTime.UtcNow,
                        CreatedAt        = DateTime.UtcNow,
                        ReceiptNumber    = $"TRS-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999):D5}",
                    };

                    _db.Payments.Add(payment);
                    await _db.SaveChangesAsync();

                    var paymentItems = await _db.PaymentItems
                        .Where(pi => pi.GroupId != 0 &&
                               _db.ParticipantGroups
                                  .Where(g => g.RegistrationId == registrationId)
                                  .Select(g => g.GroupId)
                                  .Contains(pi.GroupId))
                        .ToListAsync();

                    foreach (var item in paymentItems)
                    {
                        item.ItemStatus = "S";
                        item.PaymentId  = payment.PaymentId;
                        item.UpdatedAt  = DateTime.UtcNow;
                    }

                    registration.RegistrationStatus = "C";
                    registration.RegStatus          = "Confirmed";
                    registration.UpdatedAt          = DateTime.UtcNow;
                    registration.ConfirmedAt        = DateTime.UtcNow;

                    var groups = await _db.ParticipantGroups
                        .Where(g => g.RegistrationId == registrationId)
                        .ToListAsync();
                    foreach (var g in groups) { g.GroupStatus = "Confirmed"; g.UpdatedAt = DateTime.UtcNow; }

                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway   = "stripe",
                        GatewayEventId   = eventId,
                        EventType        = "checkout.session.completed",
                        PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "S",
                        ReceivedAt       = DateTime.UtcNow,
                        ProcessedAt      = DateTime.UtcNow
                    });

                    try { await _db.SaveChangesAsync(); }
                    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate") == true)
                    {
                        _logger.LogInformation("Duplicate webhook detected during save: {EventId}", eventId);
                        await transaction.RollbackAsync();
                        return;
                    }

                    await transaction.CommitAsync();

                    var paymentIdForJob = payment.PaymentId;
                    var regIdForJob     = registrationId;
                    await _jobQueue.EnqueueAsync(async ct =>
                    {
                        using var scope     = _serviceScopeFactory.CreateScope();
                        var receiptSvc      = scope.ServiceProvider.GetRequiredService<ReceiptService>();
                        var emailSvc        = scope.ServiceProvider.GetRequiredService<EmailService>();
                        var jobDb           = scope.ServiceProvider.GetRequiredService<TRSDbContext>();
                        try
                        {
                            var pdfBytes = await receiptSvc.GenerateAsync(jobDb, regIdForJob);
                            _logger.LogInformation("Receipt generated ({Bytes} bytes) for registration {RegId}",
                                pdfBytes.Length, regIdForJob);
                            await emailSvc.SendPaymentConfirmationAsync(jobDb, regIdForJob, pdfBytes, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to generate receipt for payment {PaymentId}", paymentIdForJob);
                        }
                    });

                    _logger.LogInformation("Successfully processed legacy payment {PaymentId} for registration {RegId}",
                        payment.PaymentId, registrationId);
                    return;
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
                    try
                    {
                        _db.WebhookLogs.Add(new WebhookLog
                        {
                            PaymentGateway   = "stripe",
                            GatewayEventId   = eventId,
                            EventType        = "checkout.session.completed",
                            PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                            ProcessingStatus = "F",
                            ErrorMessage     = ex.Message,
                            ReceivedAt       = DateTime.UtcNow,
                            ProcessedAt      = DateTime.UtcNow
                        });
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception logEx) { _logger.LogError(logEx, "Failed to log webhook error"); }
                    throw;
                }
            }
        }

        private async Task HandleCheckoutExpired(Session session, string eventId)
        {
            // Session-first flow: no registration in DB — just purge the ledger row.
            // The user never paid, so there is nothing to cancel in the DB.
            if (session.Metadata.TryGetValue("flow", out var flow) && flow == "session_first")
            {
                _logger.LogInformation(
                    "Webhook: session-first session {SessionId} expired — purging PendingCheckout row",
                    session.Id);
                try
                {
                    var pending = await _db.PendingCheckouts
                        .FindAsync(session.Id);
                    if (pending != null)
                    {
                        _db.PendingCheckouts.Remove(pending);
                        await _db.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    // Non-fatal — PaymentCleanupWorker will prune it on next run.
                    _logger.LogWarning(ex,
                        "Failed to purge PendingCheckout for expired session {SessionId}", session.Id);
                }
                return;
            }

            // Legacy flow: cancel the pending payment if it exists.
            try
            {
                var payment = await _db.Payments.FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id);
                if (payment != null && payment.PaymentStatus == "P")
                {
                    payment.PaymentStatus = "X";
                    _db.WebhookLogs.Add(new WebhookLog
                    {
                        PaymentGateway   = "stripe",
                        GatewayEventId   = eventId,
                        EventType        = "checkout.session.expired",
                        PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                        ProcessingStatus = "S",
                        ReceivedAt       = DateTime.UtcNow,
                        ProcessedAt      = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling checkout expired for session {SessionId}", session.Id);
            }
        }

        private async Task HandleChargeRefunded(Charge charge, string eventId)
        {
            var payment = await _db.Payments
                .Include(p => p.Items)
                .Include(p => p.Refunds)
                .FirstOrDefaultAsync(p => p.GatewayPaymentId == charge.PaymentIntentId || p.GatewayChargeId == charge.Id);

            if (payment == null)
            {
                _logger.LogWarning("Refund webhook received for unknown charge/payment intent {ChargeId}/{PaymentIntentId}", charge.Id, charge.PaymentIntentId);
                return;
            }

            var changed = false;
            foreach (var stripeRefund in charge.Refunds?.Data ?? Enumerable.Empty<Stripe.Refund>())
            {
                var localRefund = await _db.Refunds.FirstOrDefaultAsync(r => r.GatewayRefundId == stripeRefund.Id);
                if (localRefund == null)
                    continue;

                var newStatus = stripeRefund.Status switch
                {
                    "succeeded" => "S",
                    "failed" => "F",
                    _ => localRefund.RefundStatus
                };

                if (localRefund.RefundStatus != newStatus)
                {
                    localRefund.RefundStatus = newStatus;
                    localRefund.ProcessedAt = DateTime.UtcNow;
                    changed = true;
                }

                var item = payment.Items.FirstOrDefault(i => i.PaymentItemId == localRefund.PaymentItemId);
                if (item != null && newStatus == "S" && item.ItemStatus != "R")
                {
                    item.ItemStatus = "R";
                    item.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (!changed)
                return;

            PaymentController.ApplyRefundOutcome(payment);
            _db.WebhookLogs.Add(new WebhookLog
            {
                PaymentGateway = "stripe",
                GatewayEventId = eventId,
                EventType = "charge.refunded",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(charge),
                ProcessingStatus = "S",
                ReceivedAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Performs the same DB write as PaymentController.ConfirmSession() but driven
        /// by the Stripe webhook. Called when the user never returned to /payment/result
        /// after a successful payment — the PendingCheckout ledger row provides the payload.
        /// </summary>
        private async Task WriteSessionFirstRegistration(Session session, string payloadJson, string eventId)
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var req = System.Text.Json.JsonSerializer.Deserialize<CreateRegistrationRequest>(payloadJson, jsonOptions);
            if (req == null) throw new InvalidOperationException("Failed to deserialize PendingCheckout payload.");

            var stripeAmount = (session.AmountTotal ?? 0) / 100m;
            session.Metadata.TryGetValue("payment_method", out var paymentMethod);
            paymentMethod ??= "CreditCard";

            // Pre-load custom fields for label → ID resolution
            var programIds = req.Groups.Select(g => g.ProgramId).Distinct().ToList();
            var customFieldsByProgram = await _db.ProgramCustomFields
                .Where(cf => programIds.Contains(cf.ProgramId))
                .GroupBy(cf => cf.ProgramId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.ToDictionary(cf => cf.Label, cf => cf.CustomFieldId));

            using var tx = await _db.Database.BeginTransactionAsync();

            var reg = new EventRegistration
            {
                EventId            = req.EventId,
                EventName          = req.EventName,
                RegStatus          = "Confirmed",
                RegistrationStatus = "C",
                ContactName        = req.ContactName,
                ContactEmail       = req.ContactEmail,
                ContactPhone       = req.ContactPhone,
                TotalAmount        = stripeAmount,
                Currency           = req.Payment?.Currency ?? "SGD",
                SubmittedAt        = DateTime.UtcNow,
                CreatedAt          = DateTime.UtcNow,
                ConfirmedAt        = DateTime.UtcNow,
            };
            _db.EventRegistrations.Add(reg);
            await _db.SaveChangesAsync();

            var allItems = new List<PaymentItem>();

            foreach (var gDto in req.Groups)
            {
                // Capacity check (race-condition safe)
                var program = await _db.Programs
                    .FromSqlRaw("SELECT * FROM Programs WITH (UPDLOCK, ROWLOCK) WHERE ProgramID = {0}", gDto.ProgramId)
                    .FirstOrDefaultAsync();
                if (program == null)
                    throw new InvalidOperationException($"Program '{gDto.ProgramName}' not found.");
                if (!program.IsActive || program.Status == "closed")
                    throw new InvalidOperationException($"Program '{gDto.ProgramName}' is closed.");

                var activeGroupCount = await _db.ParticipantGroups
                    .CountAsync(g => g.ProgramId == gDto.ProgramId
                        && g.GroupStatus != "Cancelled");
                if (activeGroupCount >= program.MaxParticipants)
                    throw new InvalidOperationException($"Program '{gDto.ProgramName}' is full.");

                // Duplicate check (participant identity)
                var incomingParticipants = gDto.Participants
                    .Select(p => new
                    {
                        p.FullName,
                        Dob = string.IsNullOrWhiteSpace(p.Dob) ? (DateOnly?)null : DateOnly.Parse(p.Dob),
                    }).ToList();

                var isDuplicate = await _db.ParticipantGroups
                    .AnyAsync(g => g.ProgramId == gDto.ProgramId
                        && g.GroupStatus != "Cancelled"
                        && g.Participants.Any(existing => incomingParticipants.Any(incoming =>
                            incoming.FullName == existing.FullName
                            && incoming.Dob == existing.DateOfBirth)));
                if (isDuplicate)
                    throw new InvalidOperationException($"Duplicate participant detected for '{gDto.ProgramName}'.");

                var group = new ParticipantGroup
                {
                    RegistrationId = reg.RegistrationId,
                    EventId        = req.EventId,
                    ProgramId      = gDto.ProgramId,
                    ProgramName    = gDto.ProgramName,
                    Fee            = gDto.Fee,
                    GroupStatus    = "Confirmed",
                    CreatedAt      = DateTime.UtcNow,
                };
                _db.ParticipantGroups.Add(group);
                await _db.SaveChangesAsync();

                var parts = new List<Participant>();
                foreach (var pDto in gDto.Participants)
                {
                    var p = new Participant
                    {
                        GroupId           = group.GroupId,
                        FullName          = pDto.FullName,
                        DateOfBirth       = pDto.Dob != null ? DateOnly.Parse(pDto.Dob) : null,
                        Gender            = pDto.Gender,
                        Nationality       = pDto.Nationality,
                        ClubSchoolCompany = pDto.ClubSchoolCompany,
                        Email             = pDto.Email,
                        ContactNumber     = pDto.ContactNumber,
                        TshirtSize        = pDto.TshirtSize,
                        SbaId             = pDto.SbaId,
                        GuardianName      = pDto.GuardianName,
                        GuardianContact   = pDto.GuardianContact,
                        Remark            = pDto.Remark,
                        CreatedAt         = DateTime.UtcNow,
                    };
                    _db.Participants.Add(p);
                    parts.Add(p);
                }
                await _db.SaveChangesAsync();

                // Custom field values
                var cfLookup = customFieldsByProgram.GetValueOrDefault(gDto.ProgramId)
                               ?? new Dictionary<string, int>();
                for (int pi = 0; pi < gDto.Participants.Count; pi++)
                {
                    foreach (var (label, val) in gDto.Participants[pi].CustomFieldValues)
                    {
                        if (!cfLookup.TryGetValue(label, out var cfId))
                        {
                            _logger.LogWarning(
                                "Webhook: custom field '{Label}' not found for program {ProgramId} — skipping",
                                label, gDto.ProgramId);
                            continue;
                        }
                        _db.ParticipantCustomFieldValues.Add(new ParticipantCustomFieldValue
                        {
                            ParticipantId = parts[pi].ParticipantId,
                            CustomFieldId = cfId,
                            FieldLabel    = label,
                            FieldValue    = val,
                        });
                    }
                }

                group.ClubDisplay  = parts.FirstOrDefault()?.ClubSchoolCompany ?? "";
                group.NamesDisplay = string.Join(" / ", parts.Select(p => p.FullName));

                foreach (var iDto in gDto.Items)
                {
                    int? participantId = null;
                    if (iDto.ParticipantIndex.HasValue && iDto.ParticipantIndex < parts.Count)
                        participantId = parts[iDto.ParticipantIndex.Value].ParticipantId;

                    allItems.Add(new PaymentItem
                    {
                        GroupId       = group.GroupId,
                        EventId       = req.EventId,
                        ProgramId     = gDto.ProgramId,
                        ProgramName   = iDto.ProgramName,
                        Description   = iDto.Description,
                        PlayerName    = iDto.PlayerName,
                        Amount        = iDto.Amount,
                        ItemStatus    = "S",
                        CreatedAt     = DateTime.UtcNow,
                        ParticipantId = participantId,
                    });
                }
            }

            var receiptNo = $"TRS-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999):D5}";
            var payment = new Payment
            {
                RegistrationId   = reg.RegistrationId,
                EventId          = req.EventId,
                PaymentGateway   = "Stripe",
                PaymentMethod    = paymentMethod,
                Amount           = stripeAmount,
                Currency         = req.Payment?.Currency ?? "SGD",
                PaymentStatus    = "S",
                GatewaySessionId = session.Id,
                GatewayPaymentId = session.PaymentIntentId,
                ReceiptNumber    = receiptNo,
                CreatedAt        = DateTime.UtcNow,
                PaidAt           = DateTime.UtcNow,
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            foreach (var item in allItems) { item.PaymentId = payment.PaymentId; _db.PaymentItems.Add(item); }
            await _db.SaveChangesAsync();

            _db.WebhookLogs.Add(new WebhookLog
            {
                PaymentGateway   = "stripe",
                GatewayEventId   = eventId,
                EventType        = "checkout.session.completed",
                PayloadJson      = System.Text.Json.JsonSerializer.Serialize(session),
                ProcessingStatus = "S",
                ReceivedAt       = DateTime.UtcNow,
                ProcessedAt      = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "Webhook: recovered session-first registration {RegId} receipt {Receipt} for session {SessionId}",
                reg.RegistrationId, receiptNo, session.Id);

            // Queue receipt PDF + confirmation email
            var regIdForJob = reg.RegistrationId;
            var payIdForJob = payment.PaymentId;
            await _jobQueue.EnqueueAsync(async ct =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var receiptSvc  = scope.ServiceProvider.GetRequiredService<ReceiptService>();
                var emailSvc    = scope.ServiceProvider.GetRequiredService<EmailService>();
                var jobDb       = scope.ServiceProvider.GetRequiredService<TRSDbContext>();
                try
                {
                    var pdfBytes = await receiptSvc.GenerateAsync(jobDb, regIdForJob);
                    await emailSvc.SendPaymentConfirmationAsync(jobDb, regIdForJob, pdfBytes, ct);
                    _logger.LogInformation(
                        "Webhook: receipt generated for recovered registration {RegId}", regIdForJob);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Webhook: failed to generate receipt for payment {PaymentId}", payIdForJob);
                }
            });
        }
    }
}
