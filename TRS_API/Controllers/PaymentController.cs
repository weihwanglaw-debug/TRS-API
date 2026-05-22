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

            var currency = pricing.Value.Currency;
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

            // ── ONE ACTIVE PAYMENT LOCK RULE ─────────────────────────────────
            // Enforce: user + event = at most ONE active PendingCheckout.
            // If a non-expired row exists for this email + event, reuse its
            // Stripe session rather than creating a new one.  This prevents
            // duplicate payments from rapid retries or multiple browser tabs.
            var existingActive = await _db.PendingCheckouts
                .Where(p => p.EventId == payload.EventId
                        && p.ContactEmail == (payload.ContactEmail ?? "")
                        && p.PaymentMethod == dbMethod
                        && p.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingActive != null)
            {
                // Retrieve the existing Stripe session to get its checkout URL.
                Session? existingSession = null;
                try
                {
                    existingSession = await new SessionService().GetAsync(existingActive.GatewaySessionId);
                }
                catch (StripeException ex)
                {
                    // Session not found on Stripe (e.g. already expired there but not yet
                    // pruned locally) — fall through to create a new one.
                    _logger.LogWarning(ex,
                        "Existing PendingCheckout session {SessionId} not found on Stripe; creating new session",
                        existingActive.GatewaySessionId);
                    existingSession = null;
                }

                if (existingSession != null && existingSession.Status == "open")
                {
                    _logger.LogInformation(
                        "Reusing existing active PendingCheckout session {SessionId} for event {EventId} contact {Email}",
                        existingActive.GatewaySessionId, payload.EventId, payload.ContactEmail);

                    // Always update the stored payload to the latest cart so webhook
                    // recovery reflects current selections if the user changed anything.
                    existingActive.PayloadJson   = request.RegistrationPayload!.Value.GetRawText();
                    existingActive.PaymentMethod = dbMethod;
                    await _db.SaveChangesAsync();

                    return Ok(new
                    {
                        checkoutUrl      = existingSession.Url,
                        gatewaySessionId = existingActive.GatewaySessionId,
                        paymentMethod    = dbMethod,
                        expiresAt        = existingActive.ExpiresAt
                    });
                }

                // Session is no longer open on Stripe — remove stale local row so we
                // can create a fresh session below.
                _db.PendingCheckouts.Remove(existingActive);
                await _db.SaveChangesAsync();
            }

            // PayNow sessions expire after 30 minutes, so rotate the idempotency key on the
            // same cadence to avoid Stripe returning an expired checkout session on retry.
            var idempotencyKey = $"sf_{payload.EventId}_{method}_{payload.ContactEmail}_{(int)(totalAmount * 100)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600}";
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
                if (string.Equals(result.Code, "CHECKOUT_CONTEXT_MISSING", StringComparison.Ordinal))
                    return Conflict(new { code = result.Code, message = result.Message });

                var isNotFound = string.Equals(result.Code, "EVENT_NOT_FOUND", StringComparison.Ordinal)
                    || string.Equals(result.Code, "PROGRAM_NOT_FOUND", StringComparison.Ordinal);
                return isNotFound
                    ? NotFound(new { code = result.Code, message = result.Message })
                    : BadRequest(new { code = result.Code, message = result.Message });
            }

            // Send confirmation email when confirm-session wins the race
            // (alreadyProcessed = webhook got there first — email already sent by webhook job)
            if (!result.AlreadyProcessed)
            {
                var regIdForJob = result.RegistrationId;
                await _jobQueue.EnqueueAsync(async ct =>
                {
                    using var scope    = _serviceScopeFactory.CreateScope();
                    var receiptSvc     = scope.ServiceProvider.GetRequiredService<ReceiptService>();
                    var emailSvc       = scope.ServiceProvider.GetRequiredService<EmailService>();
                    var jobDb          = scope.ServiceProvider.GetRequiredService<TRSDbContext>();
                    try
                    {
                        var pdfBytes = await receiptSvc.GenerateAsync(jobDb, regIdForJob);
                        await emailSvc.SendPaymentConfirmationAsync(jobDb, regIdForJob, pdfBytes, ct);
                        _logger.LogInformation(
                            "Confirmation email sent for registration {RegId} via confirm-session path", regIdForJob);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to send confirmation email for registration {RegId}", regIdForJob);
                    }
                });
            }

            return Ok(new { registrationId = result.RegistrationId.ToString() });
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