using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // NO [Authorize] — public access for event registration payments
    public class PaymentController : ControllerBase
    {
        private readonly ILogger<PaymentController> _logger;
        private readonly IConfiguration _config;
        private readonly TRSDbContext _db;

        public PaymentController(
            ILogger<PaymentController> logger,
            IConfiguration config,
            TRSDbContext db)
        {
            _logger = logger;
            _config = config;
            _db = db;
            StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        }

        // ── GET /api/Payment/get-payment-info/:registrationId ─────────────────
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

        // ── POST /api/Payment/create-checkout-session ─────────────────────────
        // Unified endpoint for both card and PayNow.
        // Frontend sends: { registrationId, paymentMethod, successUrl, cancelUrl }
        // paymentMethod = "card"   → Stripe hosted checkout (Visa / Mastercard / Amex)
        // paymentMethod = "paynow" → Stripe PayNow (Singapore instant bank transfer)
        //
        // How Stripe PayNow works:
        //   1. Create a Checkout Session with PaymentMethodTypes = ["paynow"]
        //   2. Stripe hosts the QR code page — user opens banking app, scans QR
        //   3. Bank transfers funds instantly to Stripe
        //   4. Stripe fires checkout.session.completed webhook → same handler as card
        //   5. No code change needed in StripeWebhookController
        //
        // PayNow requirements in Stripe:
        //   - Your Stripe account must have PayNow enabled (Dashboard → Settings → Payment methods)
        //   - Currency must be SGD
        //   - Minimum amount SGD 0.50
        //   - No subscription/recurring support (one-time only — fine for tournament registrations)
        [EnableRateLimiting("payment")]
        [HttpPost("create-checkout-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] PaymentRequest? request)
        {
            if (request == null || request.RegistrationId <= 0)
                return BadRequest(new { message = "Invalid registration ID" });

            try
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

                // ── Determine payment method ───────────────────────────────────
                var method = (request.PaymentMethod ?? "card").ToLower().Trim();
                var isPayNow = method == "paynow";
                var stripeMethod = isPayNow ? "paynow" : "card";
                var dbMethod = isPayNow ? "PayNow" : "CreditCard";

                // PayNow requires SGD
                if (isPayNow && !registration.Currency.Equals("SGD", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { message = "PayNow is only available for SGD payments." });

                // ── Build Stripe Checkout Session ─────────────────────────────
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
                                    Description = $"Registration #{registration.RegistrationId} — {registration.EventName}"
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
                        { "registration_id", registration.RegistrationId.ToString() },
                        { "payment_method",  dbMethod }
                    }
                };

                // PayNow-specific: expire session after 30 minutes (QR codes are time-sensitive)
                if (isPayNow)
                    options.ExpiresAt = DateTime.UtcNow.AddMinutes(30);

                var requestOptions = new RequestOptions
                {
                    // Idempotency key prevents duplicate sessions if user double-clicks
                    IdempotencyKey = $"checkout_{method}_reg_{registration.RegistrationId}"
                };

                var session = await new SessionService().CreateAsync(options, requestOptions);

                _logger.LogInformation(
                    "Created {Method} Stripe session {SessionId} for registration {RegId}",
                    dbMethod, session.Id, registration.RegistrationId);

                return Ok(new
                {
                    checkoutUrl = session.Url,
                    gatewaySessionId = session.Id,
                    paymentMethod = dbMethod,
                    expiresAt = session.ExpiresAt
                });
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe error creating checkout session for registration {RegId}",
                    request.RegistrationId);

                // Translate common Stripe errors to friendly messages
                var message = ex.StripeError?.Code switch
                {
                    "payment_method_not_available" =>
                        "PayNow is not enabled on this Stripe account. Please use Credit Card or contact support.",
                    "amount_too_small" =>
                        $"Minimum payment amount is SGD 0.50.",
                    _ => "Payment gateway error. Please try again."
                };
                return StatusCode(500, new { message, code = ex.StripeError?.Code });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating checkout session for registration {RegId}",
                    request.RegistrationId);
                return StatusCode(500, new { message = "Failed to create payment session" });
            }
        }

        // ── GET /api/Payment/verify/:paymentId ────────────────────────────────
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
    }
}