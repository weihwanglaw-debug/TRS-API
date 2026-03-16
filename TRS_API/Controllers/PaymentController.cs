using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TRS_API.Models;
using TRS_Data;
using TRS_Data.Models;

namespace TRS_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // NO [Authorize] - Public access for event registration payments
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
            Stripe.StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        }

        // ===========================
        // GET PAYMENT INFO
        // ===========================
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

        // ===========================
        // CREATE STRIPE CHECKOUT SESSION
        // ===========================
  
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


                // Payment will be created ONLY in webhook after successful payment

                // Stripe session options
                var options = new SessionCreateOptions
                {
                    Mode = "payment",
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = registration.Currency.ToLower(),
                        UnitAmount = (long)(registration.TotalAmount * 100),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Event Registration",
                            Description = $"Registration ID: {registration.RegistrationId}"
                        }
                    },
                    Quantity = 1
                }
            },
                    SuccessUrl = request.SuccessUrl ??
                        $"{Request.Scheme}://{Request.Host}/payment/success?regId={registration.RegistrationId}&sid={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = request.CancelUrl ??
                        $"{Request.Scheme}://{Request.Host}/payment/cancel?regId={registration.RegistrationId}",
                    ClientReferenceId = registration.RegistrationId.ToString(), // ✅ Use registrationId
                    Metadata = new Dictionary<string, string>
            {
                { "registration_id", registration.RegistrationId.ToString() }
            }
                };

                var requestOptions = new RequestOptions
                {
                    IdempotencyKey = $"checkout_reg_{registration.RegistrationId}_{DateTime.UtcNow.Ticks}"
                };

                var session = await new SessionService().CreateAsync(options, requestOptions);

                _logger.LogInformation("Created Stripe session {SessionId} for registration {RegId}",
                    session.Id, registration.RegistrationId);

                return Ok(new { checkoutUrl = session.Url });
            }
            catch (Stripe.StripeException ex)
            {
                _logger.LogError(ex, "Stripe error creating checkout session");
                return StatusCode(500, new { message = "Payment gateway error" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating checkout session for registration {RegId}",
                    request.RegistrationId);
                return StatusCode(500, new { message = "Failed to create payment session" });
            }
        }

        // ===========================
        // CREATE PAYNOW PAYMENT
        // ===========================
        [EnableRateLimiting("payment")]
        [HttpPost("create-paynow-payment")]


        public async Task<IActionResult> CreatePayNowPayment([FromBody] PaymentRequest? request)
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

                var payment = new Payment
                {
                    RegistrationId = registration.RegistrationId,
                    Amount = registration.TotalAmount,
                    Currency = registration.Currency,
                    PaymentGateway = "paynow",
                    PaymentMethod = "paynow",
                    PaymentStatus = "P",
                    CreatedAt = DateTime.UtcNow
                };

                _db.Payments.Add(payment);
                await _db.SaveChangesAsync();

                // TODO: Integrate with PayNow gateway
                var paynowUrl = $"{Request.Scheme}://{Request.Host}/paynow/checkout?pid={payment.PaymentId}";

                _logger.LogInformation("Created PayNow payment {PaymentId} for registration {RegId}",
                    payment.PaymentId, registration.RegistrationId);

                return Ok(new { url = paynowUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating PayNow payment for registration {RegId}",
                    request.RegistrationId);
                return StatusCode(500, new { message = "Failed to create PayNow payment" });
            }
        }

        // ===========================
        // VERIFY PAYMENT STATUS
        // ===========================
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
                    paidAt = payment.PaidAt,
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