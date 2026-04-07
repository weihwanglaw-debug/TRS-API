using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

        public PaymentController(
            ILogger<PaymentController> logger,
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
            StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        }

        // -- GET /api/Payment/get-payment-info/:registrationId -----------------
        // Used by the HTML checkout page to display the amount before payment.
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
                    registrationId = registration.RegistrationId,
                    amount = registration.TotalAmount,
                    currency = registration.Currency,
                    registrationStatus = registration.RegistrationStatus,
                    isPaid = existingPayment != null,
                    message = existingPayment != null ? "Already paid" : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching payment info for registration {RegId}", registrationId);
                return StatusCode(500, new { message = "Failed to load payment information" });
            }
        }

        // -- POST /api/Payment/create-checkout-session -------------------------
        // Handles two paths:
        //
        // PATH A - Session-first (paid registrations, new flow):
        //   Frontend sends: { registrationPayload: {...}, paymentMethod, successUrl, cancelUrl }
        //   Backend: computes amount from payload, creates Stripe session, returns checkoutUrl.
        //   NO database write. DB insert happens in /api/registrations/confirm-session
        //   after the user returns from Stripe with a successful payment.
        //
        // PATH B - Legacy (free registrations, unchanged):
        //   Frontend sends: { registrationId, paymentMethod, successUrl, cancelUrl }
        //   Backend: reads amount from DB, creates Stripe session, returns checkoutUrl.
        [EnableRateLimiting("payment")]
        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] PaymentRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Invalid request" });

            try
            {
                // -- PATH A: Session-first paid flow ---------------------------
                if (request.IsSessionFirst)
                {
                    return await CreateSessionFirstCheckout(request);
                }

                // -- PATH B: Legacy free-registration flow ---------------------
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
            // Deserialize payload to compute amount server-side
            var payload = JsonSerializer.Deserialize<CreateRegistrationRequest>(
                request.RegistrationPayload!.Value.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null)
                return BadRequest(new { message = "Invalid registration payload" });

            // Compute total from groups - never trust client-sent amount
            var totalAmount = payload.Groups.Sum(g => g.Fee);
            if (totalAmount <= 0)
                return BadRequest(new { message = "Total amount must be greater than zero" });

            var currency = payload.Payment?.Currency ?? "SGD";
            var method = (request.PaymentMethod ?? "card").ToLower().Trim();
            var isPayNow = method == "paynow";
            var stripeMethod = isPayNow ? "paynow" : "card";
            var dbMethod = isPayNow ? "PayNow" : "CreditCard";

            if (isPayNow && !currency.Equals("SGD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "PayNow is only available for SGD payments." });

            var options = new SessionCreateOptions
            {
                Mode = "payment",
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
                Metadata = new Dictionary<string, string>
                {
                    // Store flow type so webhook knows this session has no pre-existing reg
                    { "flow",           "session_first" },
                    { "payment_method", dbMethod },
                    { "event_id",       payload.EventId.ToString() },
                    { "contact_email",  payload.ContactEmail ?? "" },
                }
            };

            if (isPayNow) options.ExpiresAt = DateTime.UtcNow.AddMinutes(30);

            // PayNow sessions expire after 30 minutes, so rotate the idempotency key on the
            // same cadence to avoid Stripe returning an expired checkout session on retry.
            var idempotencyKey = isPayNow
                ? $"sf_{payload.EventId}_{payload.ContactEmail}_{(int)(totalAmount * 100)}_{method}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 1800}"
                : $"sf_{payload.EventId}_{payload.ContactEmail}_{(int)(totalAmount * 100)}_{method}";
            var requestOptions = new RequestOptions { IdempotencyKey = idempotencyKey };

            var session = await new SessionService().CreateAsync(options, requestOptions);

            _logger.LogInformation(
                "Created session-first {Method} Stripe session {SessionId} for event {EventId} contact {Email}",
                dbMethod, session.Id, payload.EventId, payload.ContactEmail);

            // ── Persist payload to PendingCheckouts ledger ────────────────────
            // This is the safety net: if the user never returns to /payment/result,
            // the Stripe webhook reads this row to reconstruct and save the registration.
            //
            // UPSERT — not insert-if-missing:
            //   When the PayNow idempotency key rotates (every 30 min), Stripe creates
            //   a new session and we always INSERT. For card sessions, the stable
            //   idempotency key can cause Stripe to return an EXISTING session ID when
            //   the user retries with a different cart but the same event + email + total.
            //   In that case we must UPDATE the stored payload to the latest cart so the
            //   webhook never replays a stale one.
            //
            // FATAL on failure:
            //   If we cannot write this row we cannot guarantee recovery if the user
            //   never returns from Stripe. Rather than silently hand the user a URL with
            //   no safety net, we fail the request. The user retries and the next attempt
            //   will succeed. A DB write failure here indicates a wider infrastructure
            //   problem that should be surfaced immediately.
            var newExpiresAt   = session.ExpiresAt;
            var newPayloadJson = request.RegistrationPayload!.Value.GetRawText();

            var existing = await _db.PendingCheckouts
                .FindAsync(session.Id);

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
                // Session ID reused by Stripe (stable idempotency key, same amount).
                // Always overwrite with the latest payload so webhook recovery is current.
                existing.PayloadJson   = newPayloadJson;
                existing.ContactEmail  = payload.ContactEmail ?? "";
                existing.PaymentMethod = dbMethod;
                existing.ExpiresAt     = newExpiresAt;
            }

            // Fatal: if we cannot guarantee webhook recovery, do not give the user a
            // checkout URL. Let the exception bubble to the outer try/catch which
            // returns 500 — the user retries and the next attempt will succeed.
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

            var method = (request.PaymentMethod ?? "card").ToLower().Trim();
            var isPayNow = method == "paynow";
            var stripeMethod = isPayNow ? "paynow" : "card";
            var dbMethod = isPayNow ? "PayNow" : "CreditCard";

            if (isPayNow && !registration.Currency.Equals("SGD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "PayNow is only available for SGD payments." });

            var options = new SessionCreateOptions
            {
                Mode = "payment",
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
        // Called by PaymentResult.tsx after Stripe redirects back with success.
        // Verifies payment with Stripe, then writes Registration + Payment to DB.
        // Idempotent: if already processed, returns existing registrationId.
        [EnableRateLimiting("payment")]
        [HttpPost("confirm-session")]
        public async Task<IActionResult> ConfirmSession([FromBody] ConfirmSessionRequest request)
        {
            try
            {
                StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];

                // -- 1. Retrieve and verify session with Stripe -----------------
                var sessionService = new SessionService();
                Session session;
                try
                {
                    session = await sessionService.GetAsync(request.GatewaySessionId);
                }
                catch (StripeException ex)
                {
                    _logger.LogWarning(ex, "Stripe session not found: {SessionId}", request.GatewaySessionId);
                    return BadRequest(new { message = "Payment session not found. Please contact the organiser." });
                }

                if (session.PaymentStatus != "paid")
                {
                    _logger.LogWarning("Session {SessionId} not paid - status: {Status}",
                        request.GatewaySessionId, session.PaymentStatus);
                    return BadRequest(new { message = "Payment has not been confirmed by Stripe." });
                }

                // -- 2. Idempotency: check if already processed -----------------
                var existing = await _db.Payments
                    .Include(p => p.Registration)
                    .FirstOrDefaultAsync(p => p.GatewaySessionId == request.GatewaySessionId
                                           && p.PaymentStatus == "S");
                if (existing != null)
                {
                    _logger.LogInformation("Session {SessionId} already confirmed -> reg {RegId}",
                        request.GatewaySessionId, existing.RegistrationId);
                    return Ok(new { registrationId = existing.RegistrationId.ToString() });
                }

                // -- 3. Deserialize registration payload ------------------------
                var req = JsonSerializer.Deserialize<CreateRegistrationRequest>(
                    request.RegistrationPayload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (req == null)
                    return BadRequest(new { message = "Invalid registration payload." });

                // -- 4. Server-side amount verification -------------------------
                var computedAmount = req.Groups.Sum(g => g.Fee);
                var stripeAmount   = (session.AmountTotal ?? 0) / 100m;  // Stripe stores in cents; AmountTotal is long?
                if (Math.Abs(computedAmount - stripeAmount) > 0.01m)
                {
                    _logger.LogWarning(
                        "Amount mismatch for session {SessionId}: payload={Payload} stripe={Stripe}",
                        request.GatewaySessionId, computedAmount, stripeAmount);
                    // Log but don't block - Stripe amount is ground truth
                }

                // -- 5. Read payment method from Stripe metadata ----------------
                session.Metadata.TryGetValue("payment_method", out var paymentMethod);
                paymentMethod ??= "CreditCard";

                // -- 6. Pre-load custom fields for label -> ID resolution ------------
                var programIds = req.Groups.Select(g => g.ProgramId).Distinct().ToList();
                var customFieldsByProgram = await _db.ProgramCustomFields
                    .Where(cf => programIds.Contains(cf.ProgramId))
                    .GroupBy(cf => cf.ProgramId)
                    .ToDictionaryAsync(
                        g => g.Key,
                        g => g.ToDictionary(cf => cf.Label, cf => cf.CustomFieldId));

                // -- 7. Write to DB in one transaction ------------------------------
                using var tx = await _db.Database.BeginTransactionAsync();
                try
                {
                    // Registration
                    var reg = new EventRegistration
                    {
                        EventId            = req.EventId,
                        EventName          = req.EventName,
                        RegStatus          = "Confirmed",
                        RegistrationStatus = "C",
                        ContactName        = req.ContactName,
                        ContactEmail       = req.ContactEmail,
                        ContactPhone       = req.ContactPhone,
                        TotalAmount        = stripeAmount,  // use Stripe-confirmed amount
                        Currency           = req.Payment?.Currency ?? "SGD",
                        SubmittedAt        = DateTime.UtcNow,
                        CreatedAt          = DateTime.UtcNow,
                        ConfirmedAt        = DateTime.UtcNow,
                    };
                    _db.EventRegistrations.Add(reg);
                    await _db.SaveChangesAsync();

                    var allItems = new List<PaymentItem>();

                    // Groups + Participants
                    foreach (var gDto in req.Groups)
                    {
                        var program = await _db.Programs
                            .FromSqlRaw(
                                "SELECT * FROM Programs WITH (UPDLOCK, ROWLOCK) WHERE ProgramID = {0}",
                                gDto.ProgramId)
                            .FirstOrDefaultAsync();

                        if (program == null)
                        {
                            await tx.RollbackAsync();
                            return NotFound(new { message = $"Program '{gDto.ProgramName}' not found." });
                        }

                        if (!program.IsActive || program.Status == "closed")
                        {
                            await tx.RollbackAsync();
                            return BadRequest(new { message = $"'{gDto.ProgramName}' is no longer accepting registrations." });
                        }

                        var activeGroupCount = await _db.ParticipantGroups
                            .CountAsync(g => g.ProgramId == gDto.ProgramId
                                && g.GroupStatus != "Cancelled");

                        if (activeGroupCount >= program.MaxParticipants)
                        {
                            await tx.RollbackAsync();
                            return BadRequest(new { message = $"'{gDto.ProgramName}' is full. No slots remaining." });
                        }

                        var incomingParticipants = gDto.Participants
                            .Select(p => new
                            {
                                p.FullName,
                                Dob = string.IsNullOrWhiteSpace(p.Dob) ? (DateOnly?)null : DateOnly.Parse(p.Dob),
                            })
                            .ToList();

                        var isDuplicate = await _db.ParticipantGroups
                            .AnyAsync(g => g.ProgramId == gDto.ProgramId
                                && g.GroupStatus != "Cancelled"
                                && g.Participants.Any(existing => incomingParticipants.Any(incoming =>
                                    incoming.FullName == existing.FullName
                                    && incoming.Dob == existing.DateOfBirth)));

                        if (isDuplicate)
                        {
                            await tx.RollbackAsync();
                            return BadRequest(new { message = $"One or more participants are already registered for '{gDto.ProgramName}'." });
                        }

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
                                GroupId            = group.GroupId,
                                FullName           = pDto.FullName,
                                DateOfBirth        = pDto.Dob != null ? DateOnly.Parse(pDto.Dob) : null,
                                Gender             = pDto.Gender,
                                Nationality        = pDto.Nationality,
                                ClubSchoolCompany  = pDto.ClubSchoolCompany,
                                Email              = pDto.Email,
                                ContactNumber      = pDto.ContactNumber,
                                TshirtSize         = pDto.TshirtSize,
                                SbaId              = pDto.SbaId,
                                GuardianName       = pDto.GuardianName,
                                GuardianContact    = pDto.GuardianContact,
                                Remark             = pDto.Remark,
                                CreatedAt          = DateTime.UtcNow,
                            };
                            _db.Participants.Add(p);
                            parts.Add(p);
                        }
                        await _db.SaveChangesAsync();

                        // Custom field values
                        // Frontend sends { "Field Label": "value" } - resolve label -> CustomFieldId
                        // via pre-loaded lookup. Drop unknown labels with a warning rather than
                        // saving rows that would violate the FK on CustomFieldId.
                        var cfLookup = customFieldsByProgram.GetValueOrDefault(gDto.ProgramId)
                                       ?? new Dictionary<string, int>();
                        for (int pi = 0; pi < gDto.Participants.Count; pi++)
                        {
                            foreach (var (label, val) in gDto.Participants[pi].CustomFieldValues)
                            {
                                if (!cfLookup.TryGetValue(label, out var cfId))
                                {
                                    _logger.LogWarning(
                                        "Custom field label '{Label}' not found for program {ProgramId} - skipping",
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

                        // Payment items
                        foreach (var iDto in gDto.Items)
                        {
                            int? participantId = null;
                            if (iDto.ParticipantIndex.HasValue && iDto.ParticipantIndex < parts.Count)
                                participantId = parts[iDto.ParticipantIndex.Value].ParticipantId;

                            allItems.Add(new PaymentItem
                            {
                                GroupId     = group.GroupId,
                                EventId     = req.EventId,
                                ProgramId   = gDto.ProgramId,
                                ProgramName = iDto.ProgramName,
                                Description = iDto.Description,
                                PlayerName  = iDto.PlayerName,
                                Amount      = iDto.Amount,
                                ItemStatus  = "S",   // immediately confirmed
                                CreatedAt   = DateTime.UtcNow,
                                ParticipantId = participantId,
                            });
                        }
                    }

                    // Receipt number
                    var receiptNo = $"TRS-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999):D5}";

                    // Payment record - written as Success immediately
                    var payment = new Payment
                    {
                        RegistrationId   = reg.RegistrationId,
                        EventId          = req.EventId,
                        PaymentGateway   = "Stripe",
                        PaymentMethod    = paymentMethod,
                        Amount           = stripeAmount,
                        Currency         = req.Payment?.Currency ?? "SGD",
                        PaymentStatus    = "S",
                        GatewaySessionId = request.GatewaySessionId,
                        GatewayPaymentId = session.PaymentIntentId,
                        ReceiptNumber    = receiptNo,
                        CreatedAt        = DateTime.UtcNow,
                        PaidAt           = DateTime.UtcNow,
                    };
                    _db.Payments.Add(payment);
                    await _db.SaveChangesAsync();

                    foreach (var item in allItems)
                    {
                        item.PaymentId = payment.PaymentId;
                        _db.PaymentItems.Add(item);
                    }
                    await _db.SaveChangesAsync();

                    await tx.CommitAsync();

                    _logger.LogInformation(
                        "confirm-session: created registration {RegId} receipt {Receipt} for session {SessionId}",
                        reg.RegistrationId, receiptNo, request.GatewaySessionId);

                    // ── Purge PendingCheckout row — registration is now safely in DB ──
                    try
                    {
                        var pending = await _db.PendingCheckouts
                            .FindAsync(request.GatewaySessionId);
                        if (pending != null)
                        {
                            _db.PendingCheckouts.Remove(pending);
                            await _db.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Non-fatal: row will be cleaned up by PaymentCleanupWorker.
                        _logger.LogWarning(ex,
                            "Failed to purge PendingCheckout for session {SessionId}",
                            request.GatewaySessionId);
                    }

                    // Queue background job: generate receipt PDF + send email
                    var regIdForJob = reg.RegistrationId;
                    var payIdForJob = payment.PaymentId;
                    await _jobQueue.EnqueueAsync(async ct =>
                    {
                        using var scope = _serviceScopeFactory.CreateScope();
                        var receiptSvc = scope.ServiceProvider.GetRequiredService<ReceiptService>();
                        var emailSvc   = scope.ServiceProvider.GetRequiredService<EmailService>();
                        var jobDb      = scope.ServiceProvider.GetRequiredService<TRSDbContext>();
                        try
                        {
                            var pdfBytes = await receiptSvc.GenerateAsync(jobDb, regIdForJob);
                            await emailSvc.SendPaymentConfirmationAsync(jobDb, regIdForJob, pdfBytes, ct);
                            _logger.LogInformation("Receipt generated for registration {RegId}", regIdForJob);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to generate receipt for payment {PaymentId}", payIdForJob);
                        }
                    });

                    return Ok(new { registrationId = reg.RegistrationId.ToString() });
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();
                    _logger.LogError(ex, "Error writing registration for session {SessionId}",
                        request.GatewaySessionId);
                    return StatusCode(500, new { message = "Failed to save registration. Please contact the organiser." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in confirm-session for {SessionId}",
                    request.GatewaySessionId);
                return StatusCode(500, new { message = "An unexpected error occurred." });
            }
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
                    paymentId = payment.PaymentId,
                    registrationId = payment.RegistrationId,
                    amount = payment.Amount,
                    currency = payment.Currency,
                    status = payment.PaymentStatus,
                    method = payment.PaymentMethod,
                    paidAt = payment.PaidAt,
                    receiptNumber = payment.ReceiptNumber,
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
            var totalItems = payment.Items.Count;
            var refundedItems = payment.Items.Count(i => i.ItemStatus == "R");

            payment.PaymentStatus = refundedItems switch
            {
                0 => "S",
                var count when count >= totalItems && totalItems > 0 => "FR",
                _ => "PR",
            };
            payment.UpdatedAt = DateTime.UtcNow;
        }
    }
}
