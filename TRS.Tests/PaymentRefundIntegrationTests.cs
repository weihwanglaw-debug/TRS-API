using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using TRS_API.Controllers;
using TRS_API.Models;
using TRS_API.Services;
using TRS_Data.Models;
using StripeRefund = Stripe.Refund;
using TrsRefund = TRS_Data.Models.Refund;

namespace TRS.Tests;

[Collection("PaymentRefundIntegration")]
public sealed class PaymentRefundIntegrationTests : IAsyncLifetime
{
    private const string WebhookSecret = "whsec_test_secret";
    private readonly string _runId = $"TRS_INT_{Guid.NewGuid():N}";
    private readonly TestStripeClient _stripe;
    private ServiceProvider _services = null!;

    public PaymentRefundIntegrationTests()
    {
        _stripe = new TestStripeClient(_runId);
    }

    public async Task InitializeAsync()
    {
        StripeConfiguration.ApiKey = "sk_test_trs_integration";
        StripeConfiguration.StripeClient = _stripe;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TRSConnection"] = ConnectionString,
                ["Stripe:SecretKey"] = "sk_test_trs_integration",
                ["Stripe:WebhookSecret"] = WebhookSecret,
                ["Jwt:Secret"] = "integration-test-secret-that-is-long-enough",
                ["Jwt:Issuer"] = "TRS.Tests",
                ["Jwt:Audience"] = "TRS.Tests",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddDbContext<TRSDbContext>(options => options.UseSqlServer(ConnectionString));
        services.AddSingleton<IBackgroundJobQueue, NoopBackgroundJobQueue>();
        services.AddScoped<RegistrationWorkflowService>();
        services.AddScoped<PaymentFinalizationService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<EmailService>();
        _services = services.BuildServiceProvider();

        await using var db = CreateDb();
        await db.Database.OpenConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await CleanupAsync();
        }
        finally
        {
            await _services.DisposeAsync();
            StripeConfiguration.StripeClient = null;
        }
    }

    [Fact]
    public async Task Successful_payment_flow_creates_checkout_finalizes_registration_and_marks_payment_paid()
    {
        var seed = await SeedEventAsync(50m);
        var registration = BuildRegistration(seed, "success");

        var checkout = await CreateCheckoutSessionAsync(registration);

        Assert.StartsWith(_runId, checkout.GatewaySessionId);
        Assert.False(string.IsNullOrWhiteSpace(checkout.CheckoutUrl));

        await using (var db = CreateDb())
        {
            var pending = await db.PendingCheckouts.SingleAsync(p => p.GatewaySessionId == checkout.GatewaySessionId);
            Assert.Equal(seed.EventId, pending.EventId);
            Assert.Equal(registration.ContactEmail, pending.ContactEmail);
        }

        var session = _stripe.MarkSessionPaid(checkout.GatewaySessionId);
        await PostCheckoutCompletedWebhookAsync(session, $"{_runId}_evt_success");

        await using (var db = CreateDb())
        {
            var payment = await db.Payments
                .Include(p => p.Registration)
                .Include(p => p.Items)
                .SingleAsync(p => p.GatewaySessionId == checkout.GatewaySessionId);

            Assert.Equal("S", payment.PaymentStatus);
            Assert.Equal(50m, payment.Amount);
            Assert.Equal("Confirmed", payment.Registration.RegStatus);
            Assert.Equal("C", payment.Registration.RegistrationStatus);
            Assert.Single(payment.Items);
            Assert.All(payment.Items, item => Assert.Equal("S", item.ItemStatus));
            Assert.False(await db.PendingCheckouts.AnyAsync(p => p.GatewaySessionId == checkout.GatewaySessionId));
        }
    }

    [Fact]
    public async Task Delayed_webhook_after_confirm_session_does_not_duplicate_registration_or_payment()
    {
        var seed = await SeedEventAsync(50m);
        var registration = BuildRegistration(seed, "delayed-webhook");
        var checkout = await CreateCheckoutSessionAsync(registration);
        var session = _stripe.MarkSessionPaid(checkout.GatewaySessionId);

        var confirm = await CreatePaymentController().ConfirmSession(new ConfirmSessionRequest
        {
            GatewaySessionId = checkout.GatewaySessionId,
            RegistrationPayload = JsonSerializer.SerializeToElement(registration),
        });
        Assert.IsType<OkObjectResult>(confirm);

        await PostCheckoutCompletedWebhookAsync(session, $"{_runId}_evt_delayed_after_confirm");

        await using var db = CreateDb();
        Assert.Equal(1, await db.Payments.CountAsync(p => p.GatewaySessionId == checkout.GatewaySessionId));
        Assert.Equal(1, await db.EventRegistrations.CountAsync(r => r.EventName == seed.EventName));
        var payment = await db.Payments.SingleAsync(p => p.GatewaySessionId == checkout.GatewaySessionId);
        Assert.Equal("S", payment.PaymentStatus);
    }

    [Fact]
    public async Task Webhook_before_pending_checkout_can_recover_on_retry_after_context_exists()
    {
        var seed = await SeedEventAsync(50m);
        var registration = BuildRegistration(seed, "early-webhook");
        var session = _stripe.RegisterPaidSession($"{_runId}_cs_early", $"{_runId}_pi_early", 5000);
        var eventId = $"{_runId}_evt_early_before_pending";

        await PostCheckoutCompletedWebhookAsync(session, eventId);

        await using (var db = CreateDb())
        {
            Assert.False(await db.Payments.AnyAsync(p => p.GatewaySessionId == session.Id));
            Assert.False(await db.EventRegistrations.AnyAsync(r => r.EventName == seed.EventName));
            var failure = await db.WebhookLogs.SingleAsync(w => w.GatewayEventId == eventId);
            Assert.Equal("F", failure.ProcessingStatus);
            Assert.Contains("CHECKOUT_CONTEXT_MISSING", failure.ErrorMessage);

            db.PendingCheckouts.Add(new PendingCheckout
            {
                GatewaySessionId = session.Id,
                EventId = seed.EventId,
                ContactEmail = registration.ContactEmail,
                PayloadJson = JsonSerializer.Serialize(registration),
                PaymentMethod = "CreditCard",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        await PostCheckoutCompletedWebhookAsync(session, eventId);

        await using (var db = CreateDb())
        {
            Assert.Equal(1, await db.Payments.CountAsync(p => p.GatewaySessionId == session.Id));
            Assert.Equal(1, await db.EventRegistrations.CountAsync(r => r.EventName == seed.EventName));
            Assert.Equal("S", (await db.WebhookLogs.SingleAsync(w => w.GatewayEventId == eventId)).ProcessingStatus);
            Assert.False(await db.PendingCheckouts.AnyAsync(p => p.GatewaySessionId == session.Id));
        }
    }

    [Fact]
    public async Task Partial_refund_updates_item_refund_total_and_marks_payment_partially_refunded()
    {
        var payment = await CreatePaidRegistrationAsync(50m, "partial");
        var item = payment.Items.Single();

        var result = await CreateRegistrationsController().InitiateRefund(
            payment.RegistrationId,
            new InitiateRefundRequest
            {
                PaymentItemId = item.PaymentItemId,
                RefundAmount = 20m,
                RefundReason = $"{_runId} partial refund",
            });

        Assert.IsType<OkObjectResult>(result);

        await using var db = CreateDb();
        var refreshed = await db.Payments
            .Include(p => p.Items)
            .Include(p => p.Refunds)
            .SingleAsync(p => p.PaymentId == payment.PaymentId);
        var refreshedItem = refreshed.Items.Single();
        var itemRefunded = await db.Refunds
            .Where(r => r.PaymentItemId == refreshedItem.PaymentItemId && r.RefundStatus == "S")
            .SumAsync(r => (decimal?)r.RefundAmount) ?? 0m;

        Assert.Equal(20m, itemRefunded);
        Assert.Equal("S", refreshedItem.ItemStatus);
        Assert.Equal("PR", refreshed.PaymentStatus);
    }

    [Fact]
    public async Task Full_refund_matches_payment_amount_and_marks_payment_fully_refunded()
    {
        var payment = await CreatePaidRegistrationAsync(50m, "full");
        var item = payment.Items.Single();

        var result = await CreateRegistrationsController().InitiateRefund(
            payment.RegistrationId,
            new InitiateRefundRequest
            {
                PaymentItemId = item.PaymentItemId,
                RefundAmount = 50m,
                RefundReason = $"{_runId} full refund",
            });

        Assert.IsType<OkObjectResult>(result);

        await using var db = CreateDb();
        var refreshed = await db.Payments
            .Include(p => p.Items)
            .SingleAsync(p => p.PaymentId == payment.PaymentId);
        var totalRefunded = await db.Refunds
            .Where(r => r.PaymentId == refreshed.PaymentId && r.RefundStatus == "S")
            .SumAsync(r => (decimal?)r.RefundAmount) ?? 0m;

        Assert.Equal(refreshed.Amount, totalRefunded);
        Assert.Equal("R", refreshed.Items.Single().ItemStatus);
        Assert.Equal("FR", refreshed.PaymentStatus);
    }

    [Fact]
    public async Task Duplicate_webhook_does_not_create_duplicate_payments_or_registrations()
    {
        var seed = await SeedEventAsync(50m);
        var registration = BuildRegistration(seed, "duplicate-webhook");
        var checkout = await CreateCheckoutSessionAsync(registration);
        var session = _stripe.MarkSessionPaid(checkout.GatewaySessionId);
        var eventId = $"{_runId}_evt_duplicate";

        await PostCheckoutCompletedWebhookAsync(session, eventId);
        await PostCheckoutCompletedWebhookAsync(session, eventId);

        await using var db = CreateDb();
        Assert.Equal(1, await db.Payments.CountAsync(p => p.GatewaySessionId == checkout.GatewaySessionId));
        Assert.Equal(1, await db.EventRegistrations.CountAsync(r => r.EventName == seed.EventName));
        Assert.Equal(1, await db.WebhookLogs.CountAsync(w => w.GatewayEventId == eventId));
    }

    [Fact]
    public async Task Duplicate_refund_requests_running_concurrently_create_only_one_refund()
    {
        var payment = await CreatePaidRegistrationAsync(50m, "duplicate-refund");
        var item = payment.Items.Single();

        var calls = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
                await CreateRegistrationsController().InitiateRefund(
                    payment.RegistrationId,
                    new InitiateRefundRequest
                    {
                        PaymentItemId = item.PaymentItemId,
                        RefundAmount = 50m,
                        RefundReason = $"{_runId} duplicate refund",
                    })))
            .ToArray();

        await Task.WhenAll(calls);

        await using var db = CreateDb();
        var successfulRefunds = await db.Refunds
            .Where(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "S")
            .ToListAsync();

        Assert.Single(successfulRefunds);
        Assert.Equal(50m, successfulRefunds.Sum(r => r.RefundAmount));
    }

    [Fact]
    public async Task Concurrent_orphan_refund_requests_create_only_one_refund_record()
    {
        var sessionId = $"{_runId}_cs_orphan";
        var paymentIntentId = $"{_runId}_pi_orphan";
        _stripe.RegisterPaidSession(sessionId, paymentIntentId, 5000);

        int webhookLogId;
        await using (var db = CreateDb())
        {
            var log = new WebhookLog
            {
                PaymentGateway = "stripe",
                GatewayEventId = $"{_runId}_evt_orphan_failed",
                GatewaySessionId = sessionId,
                EventType = "checkout.session.completed",
                PayloadJson = "{}",
                ProcessingStatus = "F",
                ErrorMessage = "CHECKOUT_CONTEXT_MISSING",
                Amount = 50m,
                Currency = "SGD",
                ContactEmail = $"{_runId}@example.test",
                ReceivedAt = DateTime.UtcNow,
            };
            db.WebhookLogs.Add(log);
            await db.SaveChangesAsync();
            webhookLogId = log.WebhookLogId;
        }

        var calls = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
                await CreateAdminPaymentReconciliationController().RefundOrphanedPayment(
                    webhookLogId,
                    new OrphanRefundRequest
                    {
                        Reason = $"{_runId} orphan refund",
                        AdminNote = "concurrency test",
                    })))
            .ToArray();

        await Task.WhenAll(calls);

        await using var verifyDb = CreateDb();
        var refunds = await verifyDb.Refunds
            .Where(r => r.GatewaySessionId == sessionId)
            .ToListAsync();
        var logAfter = await verifyDb.WebhookLogs.SingleAsync(w => w.WebhookLogId == webhookLogId);

        Assert.Single(refunds);
        Assert.Equal("S", refunds.Single().RefundStatus);
        Assert.Equal(50m, refunds.Single().RefundAmount);
        Assert.Equal("S", logAfter.ProcessingStatus);
    }

    [Fact]
    public async Task Webhook_mid_processing_failure_rolls_back_and_retry_recovers_without_manual_fix()
    {
        var seed = await SeedEventAsync(50m);
        var registration = BuildRegistration(seed, "rollback-retry");
        var checkout = await CreateCheckoutSessionAsync(registration);
        var session = _stripe.MarkSessionPaid(checkout.GatewaySessionId);
        var eventId = $"{_runId}_evt_forced_rollback";

        await using (var failingDb = CreateFailingDb(failOnSaveNumber: 1))
        {
            await PostCheckoutCompletedWebhookAsync(
                session,
                eventId,
                CreateStripeWebhookController(failingDb),
                expectedStatusCode: StatusCodes.Status500InternalServerError);
        }

        await using (var db = CreateDb())
        {
            Assert.False(await db.Payments.AnyAsync(p => p.GatewaySessionId == checkout.GatewaySessionId));
            Assert.False(await db.EventRegistrations.AnyAsync(r => r.EventName == seed.EventName));
            var failedLog = await db.WebhookLogs.SingleAsync(w => w.GatewayEventId == eventId);
            Assert.Equal("F", failedLog.ProcessingStatus);
        }

        await PostCheckoutCompletedWebhookAsync(session, eventId);

        await using (var db = CreateDb())
        {
            Assert.Equal(1, await db.Payments.CountAsync(p => p.GatewaySessionId == checkout.GatewaySessionId));
            Assert.Equal(1, await db.EventRegistrations.CountAsync(r => r.EventName == seed.EventName));
            Assert.Equal("S", (await db.WebhookLogs.SingleAsync(w => w.GatewayEventId == eventId)).ProcessingStatus);
        }
    }

    [Fact]
    public async Task Partial_refund_webhook_before_local_refund_insert_is_harmless_and_replay_reconciles()
    {
        var payment = await CreatePaidRegistrationAsync(50m, "early-refund-webhook");
        var item = payment.Items.Single();
        var refundId = $"{_runId}_re_early";
        var eventId = $"{_runId}_evt_refund_before_local";

        await PostChargeRefundedWebhookAsync(payment.GatewayPaymentId!, refundId, 2000, eventId);

        await using (var db = CreateDb())
        {
            var unchanged = await db.Payments.SingleAsync(p => p.PaymentId == payment.PaymentId);
            Assert.Equal("S", unchanged.PaymentStatus);
            Assert.False(await db.Refunds.AnyAsync(r => r.GatewayRefundId == refundId));

            db.Refunds.Add(new TrsRefund
            {
                PaymentId = payment.PaymentId,
                PaymentItemId = item.PaymentItemId,
                PaymentGateway = "Stripe",
                GatewayRefundId = refundId,
                RefundAmount = 20m,
                RefundReason = $"{_runId} early refund webhook",
                RefundStatus = "P",
                RequestedBy = "refund-admin",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await PostChargeRefundedWebhookAsync(payment.GatewayPaymentId!, refundId, 2000, $"{eventId}_retry");

        await using (var db = CreateDb())
        {
            var reconciled = await db.Payments
                .Include(p => p.Items)
                .SingleAsync(p => p.PaymentId == payment.PaymentId);
            var refund = await db.Refunds.SingleAsync(r => r.GatewayRefundId == refundId);

            Assert.Equal("S", refund.RefundStatus);
            Assert.Equal("PR", reconciled.PaymentStatus);
            Assert.Equal("S", reconciled.Items.Single().ItemStatus);
        }
    }

    private async Task<Payment> CreatePaidRegistrationAsync(decimal amount, string suffix)
    {
        var seed = await SeedEventAsync(amount);
        var checkout = await CreateCheckoutSessionAsync(BuildRegistration(seed, suffix));
        var session = _stripe.MarkSessionPaid(checkout.GatewaySessionId);
        await PostCheckoutCompletedWebhookAsync(session, $"{checkout.GatewaySessionId}_evt_completed");

        await using var db = CreateDb();
        return await db.Payments
            .AsNoTracking()
            .Include(p => p.Items)
            .SingleAsync(p => p.GatewaySessionId == checkout.GatewaySessionId);
    }

    private async Task<SeedData> SeedEventAsync(decimal fee)
    {
        await using var db = CreateDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var evt = new TRS_Data.Models.Event
        {
            Name = $"{_runId} Tournament",
            Venue = "Integration Test Court",
            EventStartDate = today.AddDays(30),
            EventEndDate = today.AddDays(31),
            OpenDate = today.AddDays(-1),
            CloseDate = today.AddDays(10),
            MaxParticipants = 100,
            IsSports = true,
            SportType = "Badminton",
            FixtureMode = "internal",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var program = new TrsProgram
        {
            EventId = evt.EventId,
            Name = $"{_runId} Singles",
            Type = "Singles",
            MinAge = 5,
            MaxAge = 99,
            Gender = "Any",
            Fee = fee,
            PaymentRequired = true,
            FeeStructure = "per_entry",
            SbaRequired = false,
            MinPlayers = 1,
            MaxPlayers = 1,
            MinParticipants = 1,
            MaxParticipants = 100,
            Status = "open",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Programs.Add(program);
        await db.SaveChangesAsync();

        return new SeedData(evt.EventId, evt.Name, program.ProgramId, program.Name, fee);
    }

    private CreateRegistrationRequest BuildRegistration(SeedData seed, string suffix) => new()
    {
        EventId = seed.EventId,
        EventName = seed.EventName,
        ContactName = $"{_runId} Contact {suffix}",
        ContactEmail = $"{_runId}.{suffix}@example.test",
        ContactPhone = "99999999",
        Groups =
        [
            new CreateGroupDto
            {
                ProgramId = seed.ProgramId,
                ProgramName = seed.ProgramName,
                Fee = seed.Fee,
                Participants =
                [
                    new CreateParticipantDto
                    {
                        FullName = $"{_runId} Player {suffix}",
                        Dob = "2000-01-01",
                        Gender = "Male",
                        Nationality = "Singapore",
                        ClubSchoolCompany = "TRS Integration",
                        Email = $"{_runId}.player.{suffix}@example.test",
                        ContactNumber = "99999999",
                    },
                ],
            },
        ],
        Payment = new CreatePaymentDto
        {
            Gateway = "Stripe",
            Method = "CreditCard",
            Amount = seed.Fee,
            Currency = "SGD",
        },
    };

    private async Task<CheckoutResult> CreateCheckoutSessionAsync(CreateRegistrationRequest registration)
    {
        var request = new PaymentRequest
        {
            RegistrationPayload = JsonSerializer.SerializeToElement(registration),
            PaymentMethod = "card",
            SuccessUrl = "https://localhost/success",
            CancelUrl = "https://localhost/cancel",
        };

        var result = await CreatePaymentController().CreateCheckoutSession(request);
        var ok = Assert.IsType<OkObjectResult>(result);

        return new CheckoutResult(
            ReadString(ok.Value!, "gatewaySessionId"),
            ReadString(ok.Value!, "checkoutUrl"));
    }

    private async Task PostCheckoutCompletedWebhookAsync(
        Session session,
        string eventId,
        StripeWebhookController? controllerOverride = null,
        int expectedStatusCode = StatusCodes.Status200OK)
    {
        var payload = BuildCheckoutCompletedEventJson(session, eventId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = EventUtility.ComputeSignature(WebhookSecret, timestamp, payload);
        var fakeClient = StripeConfiguration.StripeClient;
        StripeConfiguration.StripeClient = new StripeClient("sk_test_webhook_parse_only");
        try
        {
            var parsedEvent = EventUtility.ConstructEvent(payload, $"t={timestamp},v1={signature}", WebhookSecret);
            if (parsedEvent.Data.Object is not Session parsedSession || parsedSession.Metadata == null)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Test webhook payload did not deserialize as a Stripe checkout session with metadata. " +
                    $"Actual object type: {parsedEvent.Data.Object?.GetType().FullName ?? "<null>"}.");
            }

            var controller = controllerOverride ?? CreateStripeWebhookController();
            var http = new DefaultHttpContext();
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            http.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
            controller.ControllerContext = new ControllerContext { HttpContext = http };

            var result = await controller.Webhook();
            var actualStatusCode = result switch
            {
                OkResult => StatusCodes.Status200OK,
                StatusCodeResult statusCode => statusCode.StatusCode,
                _ => StatusCodes.Status200OK,
            };

            if (actualStatusCode != expectedStatusCode)
            {
                await using var db = CreateDb();
                var log = await db.WebhookLogs
                    .Where(w => w.GatewayEventId == eventId ||
                                w.GatewayEventId == "unknown" ||
                                w.PayloadJson.Contains(session.Id) ||
                                w.GatewaySessionId == session.Id)
                    .OrderByDescending(w => w.ReceivedAt)
                    .FirstOrDefaultAsync();
                throw new Xunit.Sdk.XunitException(
                    $"Expected Stripe webhook to return {expectedStatusCode}, got {actualStatusCode}. " +
                    $"WebhookLog status={log?.ProcessingStatus ?? "<none>"} error={log?.ErrorMessage ?? "<none>"}.");
            }
        }
        finally
        {
            StripeConfiguration.StripeClient = fakeClient;
        }
    }

    private string BuildCheckoutCompletedEventJson(Session session, string eventId) =>
        $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2025-12-15.clover",
          "type": "checkout.session.completed",
          "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
          "livemode": false,
          "pending_webhooks": 1,
          "request": {
            "id": null,
            "idempotency_key": null
          },
          "data": {
            "object": {
              "id": "{{session.Id}}",
              "object": "checkout.session",
              "mode": "payment",
              "status": "complete",
              "payment_status": "paid",
              "amount_total": {{session.AmountTotal}},
              "currency": "{{session.Currency}}",
              "payment_intent": "{{session.PaymentIntentId}}",
              "metadata": {
                "flow": "session_first",
                "payment_method": "CreditCard",
                "contact_email": "{{session.Metadata["contact_email"]}}",
                "contact_name": "{{session.Metadata["contact_name"]}}",
                "contact_phone": "{{session.Metadata["contact_phone"]}}"
              }
            }
          }
        }
        """;

    private async Task PostChargeRefundedWebhookAsync(
        string paymentIntentId,
        string refundId,
        long amount,
        string eventId)
    {
        var payload = BuildChargeRefundedEventJson(paymentIntentId, refundId, amount, eventId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = EventUtility.ComputeSignature(WebhookSecret, timestamp, payload);
        var fakeClient = StripeConfiguration.StripeClient;
        StripeConfiguration.StripeClient = new StripeClient("sk_test_webhook_parse_only");
        try
        {
            var parsedEvent = EventUtility.ConstructEvent(payload, $"t={timestamp},v1={signature}", WebhookSecret);
            if (parsedEvent.Data.Object is not Charge)
                throw new Xunit.Sdk.XunitException($"Test webhook payload did not deserialize as Charge.");

            var controller = CreateStripeWebhookController();
            var http = new DefaultHttpContext();
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            http.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
            controller.ControllerContext = new ControllerContext { HttpContext = http };

            var result = await controller.Webhook();
            Assert.IsType<OkResult>(result);
        }
        finally
        {
            StripeConfiguration.StripeClient = fakeClient;
        }
    }

    private static string BuildChargeRefundedEventJson(
        string paymentIntentId,
        string refundId,
        long amount,
        string eventId) =>
        $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2025-12-15.clover",
          "type": "charge.refunded",
          "created": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},
          "livemode": false,
          "pending_webhooks": 1,
          "request": {
            "id": null,
            "idempotency_key": null
          },
          "data": {
            "object": {
              "id": "{{paymentIntentId}}_ch",
              "object": "charge",
              "payment_intent": "{{paymentIntentId}}",
              "amount": 5000,
              "amount_refunded": {{amount}},
              "currency": "sgd",
              "paid": true,
              "refunded": false,
              "refunds": {
                "object": "list",
                "data": [
                  {
                    "id": "{{refundId}}",
                    "object": "refund",
                    "amount": {{amount}},
                    "currency": "sgd",
                    "payment_intent": "{{paymentIntentId}}",
                    "status": "succeeded"
                  }
                ],
                "has_more": false,
                "url": "/v1/charges/{{paymentIntentId}}_ch/refunds"
              }
            }
          }
        }
        """;

    private PaymentController CreatePaymentController()
    {
        var scope = _services.CreateScope();
        var controller = new PaymentController(
            scope.ServiceProvider.GetRequiredService<ILogger<PaymentController>>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            scope.ServiceProvider.GetRequiredService<TRSDbContext>(),
            scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<RegistrationWorkflowService>(),
            scope.ServiceProvider.GetRequiredService<PaymentFinalizationService>());
        controller.ControllerContext = ControllerContextFor("payment-admin");
        return controller;
    }

    private RegistrationsController CreateRegistrationsController()
    {
        var scope = _services.CreateScope();
        var controller = new RegistrationsController(
            scope.ServiceProvider.GetRequiredService<TRSDbContext>(),
            scope.ServiceProvider.GetRequiredService<ILogger<RegistrationsController>>(),
            null!,
            scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<RegistrationWorkflowService>());
        controller.ControllerContext = ControllerContextFor("refund-admin");
        return controller;
    }

    private StripeWebhookController CreateStripeWebhookController()
    {
        var scope = _services.CreateScope();
        return new StripeWebhookController(
            scope.ServiceProvider.GetRequiredService<ILogger<StripeWebhookController>>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            scope.ServiceProvider.GetRequiredService<TRSDbContext>(),
            scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<PaymentFinalizationService>());
    }

    private StripeWebhookController CreateStripeWebhookController(TRSDbContext db)
    {
        var registrationWorkflow = new RegistrationWorkflowService(
            db,
            _services.GetRequiredService<ILogger<RegistrationWorkflowService>>(),
            _services.GetRequiredService<IBackgroundJobQueue>(),
            _services.GetRequiredService<IServiceScopeFactory>());
        var paymentFinalization = new PaymentFinalizationService(
            db,
            registrationWorkflow,
            _services.GetRequiredService<ILogger<PaymentFinalizationService>>());

        return new StripeWebhookController(
            _services.GetRequiredService<ILogger<StripeWebhookController>>(),
            _services.GetRequiredService<IConfiguration>(),
            db,
            _services.GetRequiredService<IBackgroundJobQueue>(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            paymentFinalization);
    }

    private AdminPaymentReconciliationController CreateAdminPaymentReconciliationController()
    {
        var scope = _services.CreateScope();
        var controller = new AdminPaymentReconciliationController(
            scope.ServiceProvider.GetRequiredService<TRSDbContext>(),
            scope.ServiceProvider.GetRequiredService<ILogger<AdminPaymentReconciliationController>>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>());
        controller.ControllerContext = ControllerContextFor("orphan-admin");
        return controller;
    }

    private static ControllerContext ControllerContextFor(string userName)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.Role, "superadmin"),
        ], "IntegrationTest"));

        var http = new DefaultHttpContext { User = user };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("localhost");
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return new ControllerContext { HttpContext = http };
    }

    private TRSDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TRSDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new TRSDbContext(options);
    }

    private FailingSaveChangesDbContext CreateFailingDb(int failOnSaveNumber)
    {
        var options = new DbContextOptionsBuilder<TRSDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new FailingSaveChangesDbContext(options, failOnSaveNumber);
    }

    private async Task CleanupAsync()
    {
        await using var db = CreateDb();
        var eventIds = await db.Events
            .Where(e => e.Name.StartsWith(_runId))
            .Select(e => e.EventId)
            .ToListAsync();
        var regIds = await db.EventRegistrations
            .Where(r => eventIds.Contains(r.EventId) || r.EventName.StartsWith(_runId))
            .Select(r => r.RegistrationId)
            .ToListAsync();
        var paymentIds = await db.Payments
            .Where(p => regIds.Contains(p.RegistrationId) || (p.GatewaySessionId != null && p.GatewaySessionId.StartsWith(_runId)))
            .Select(p => p.PaymentId)
            .ToListAsync();
        var groupIds = await db.ParticipantGroups
            .Where(g => regIds.Contains(g.RegistrationId) || eventIds.Contains(g.EventId))
            .Select(g => g.GroupId)
            .ToListAsync();
        var participantIds = await db.Participants
            .Where(p => groupIds.Contains(p.GroupId))
            .Select(p => p.ParticipantId)
            .ToListAsync();
        var programIds = await db.Programs
            .Where(p => eventIds.Contains(p.EventId) || p.Name.StartsWith(_runId))
            .Select(p => p.ProgramId)
            .ToListAsync();

        await db.PaymentAuditLogs
            .Where(a => a.Reason != null && a.Reason.StartsWith(_runId))
            .ExecuteDeleteAsync();
        await db.Refunds
            .Where(r => paymentIds.Contains(r.PaymentId ?? -1) || (r.GatewaySessionId != null && r.GatewaySessionId.StartsWith(_runId)))
            .ExecuteDeleteAsync();
        await db.WebhookLogs
            .Where(w => w.GatewayEventId.StartsWith(_runId) || (w.GatewaySessionId != null && w.GatewaySessionId.StartsWith(_runId)))
            .ExecuteDeleteAsync();
        await db.PaymentItems
            .Where(i => paymentIds.Contains(i.PaymentId))
            .ExecuteDeleteAsync();
        await db.Payments
            .Where(p => paymentIds.Contains(p.PaymentId))
            .ExecuteDeleteAsync();
        await db.ParticipantCustomFieldValues
            .Where(v => participantIds.Contains(v.ParticipantId))
            .ExecuteDeleteAsync();
        await db.Participants
            .Where(p => groupIds.Contains(p.GroupId))
            .ExecuteDeleteAsync();
        await db.ParticipantGroups
            .Where(g => groupIds.Contains(g.GroupId))
            .ExecuteDeleteAsync();
        await db.EventRegistrations
            .Where(r => regIds.Contains(r.RegistrationId))
            .ExecuteDeleteAsync();
        await db.ProgramCustomFields
            .Where(f => programIds.Contains(f.ProgramId))
            .ExecuteDeleteAsync();
        await db.ProgramFields
            .Where(f => programIds.Contains(f.ProgramId))
            .ExecuteDeleteAsync();
        await db.Programs
            .Where(p => programIds.Contains(p.ProgramId))
            .ExecuteDeleteAsync();
        await db.Events
            .Where(e => eventIds.Contains(e.EventId))
            .ExecuteDeleteAsync();
    }

    private static string ReadString(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Missing property '{propertyName}'.");
        return property.GetValue(source)?.ToString()
            ?? throw new InvalidOperationException($"Property '{propertyName}' is null.");
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TRS_TEST_CONNECTION_STRING")
        ?? "Server=LAPTOP-3B6R8DFK\\SQLEXPRESS;Database=TRS;Trusted_Connection=True;TrustServerCertificate=True";

    private sealed record SeedData(int EventId, string EventName, int ProgramId, string ProgramName, decimal Fee);
    private sealed record CheckoutResult(string GatewaySessionId, string CheckoutUrl);

    private sealed class NoopBackgroundJobQueue : IBackgroundJobQueue
    {
        public ValueTask EnqueueAsync(Func<CancellationToken, Task> job) => ValueTask.CompletedTask;
        public ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
            throw new NotSupportedException("Integration tests do not execute queued background jobs.");
    }

    private sealed class TestStripeClient : IStripeClient
    {
        private readonly string _runId;
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly ConcurrentDictionary<string, StripeRefund> _refundsByIdempotencyKey = new();
        private int _sessionSequence;
        private int _refundSequence;

        public TestStripeClient(string runId)
        {
            _runId = runId;
        }

        public string ApiBase => "https://api.stripe.test";
        public string ApiKey => "sk_test_trs_integration";
        public string ClientId => "ca_test";
        public string ConnectBase => "https://connect.stripe.test";
        public string FilesBase => "https://files.stripe.test";
        public string MeterEventsBase => "https://meter-events.stripe.test";

        public Task<T> RequestAsync<T>(
            System.Net.Http.HttpMethod method,
            string path,
            BaseOptions options,
            RequestOptions requestOptions,
            CancellationToken cancellationToken = default)
            where T : IStripeEntity
        {
            object response;
            if (typeof(T) == typeof(Session))
                response = HandleSessionRequest(method, path, options);
            else if (typeof(T) == typeof(StripeRefund))
                response = HandleRefundRequest(options, requestOptions);
            else
                throw new NotSupportedException($"Stripe fake does not support {typeof(T).FullName} at {path}.");

            return Task.FromResult((T)response);
        }

        public Task<Stream> RequestStreamingAsync(
            System.Net.Http.HttpMethod method,
            string path,
            BaseOptions options,
            RequestOptions requestOptions,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming Stripe requests are not used by these tests.");

        public Session MarkSessionPaid(string sessionId)
        {
            var session = _sessions[sessionId];
            session.PaymentStatus = "paid";
            session.Status = "complete";
            session.PaymentIntentId ??= $"{sessionId}_pi";
            return session;
        }

        public Session RegisterPaidSession(string sessionId, string paymentIntentId, long amountTotal)
        {
            var session = new Session
            {
                Id = sessionId,
                Url = $"https://checkout.stripe.test/{sessionId}",
                Status = "complete",
                PaymentStatus = "paid",
                AmountTotal = amountTotal,
                Currency = "sgd",
                PaymentIntentId = paymentIntentId,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                Metadata = new Dictionary<string, string>
                {
                    ["flow"] = "session_first",
                    ["payment_method"] = "CreditCard",
                    ["contact_email"] = $"{sessionId}@example.test",
                    ["contact_name"] = "Orphan Payer",
                    ["contact_phone"] = "99999999",
                },
            };
            _sessions[sessionId] = session;
            return session;
        }

        private Session HandleSessionRequest(System.Net.Http.HttpMethod method, string path, BaseOptions options)
        {
            if (method == System.Net.Http.HttpMethod.Post && path == "/v1/checkout/sessions")
            {
                var createOptions = (SessionCreateOptions)options;
                var amount = createOptions.LineItems?.Single().PriceData?.UnitAmount ?? 0;
                var id = $"{_runId}_cs_{Interlocked.Increment(ref _sessionSequence):D4}";
                var session = new Session
                {
                    Id = id,
                    Url = $"https://checkout.stripe.test/{id}",
                    Status = "open",
                    PaymentStatus = "unpaid",
                    AmountTotal = amount,
                    Currency = createOptions.LineItems?.Single().PriceData?.Currency ?? "sgd",
                    PaymentIntentId = $"{id}_pi",
                    ExpiresAt = createOptions.ExpiresAt ?? DateTime.UtcNow.AddHours(1),
                    Metadata = createOptions.Metadata ?? new Dictionary<string, string>(),
                };
                _sessions[id] = session;
                return session;
            }

            if (method == System.Net.Http.HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
            {
                var sessionId = Uri.UnescapeDataString(path["/v1/checkout/sessions/".Length..]);
                return _sessions[sessionId];
            }

            throw new NotSupportedException($"Unsupported Stripe session request {method} {path}.");
        }

        private StripeRefund HandleRefundRequest(BaseOptions options, RequestOptions requestOptions)
        {
            var idempotencyKey = requestOptions?.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            return _refundsByIdempotencyKey.GetOrAdd(idempotencyKey, _ =>
            {
                var refundOptions = (RefundCreateOptions)options;
                return new StripeRefund
                {
                    Id = $"re_{Interlocked.Increment(ref _refundSequence):D6}",
                    Amount = refundOptions.Amount ?? 0,
                    PaymentIntentId = refundOptions.PaymentIntent,
                    Status = "succeeded",
                };
            });
        }
    }

    private sealed class FailingSaveChangesDbContext : TRSDbContext
    {
        private readonly int _failOnSaveNumber;
        private int _saveCount;

        public FailingSaveChangesDbContext(
            DbContextOptions<TRSDbContext> options,
            int failOnSaveNumber)
            : base(options)
        {
            _failOnSaveNumber = failOnSaveNumber;
        }

        public override async Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            _saveCount++;
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            if (_saveCount == _failOnSaveNumber)
                throw new DbUpdateException("Forced integration-test failure after partial registration work.");

            return result;
        }
    }
}

[CollectionDefinition("PaymentRefundIntegration", DisableParallelization = true)]
public sealed class PaymentRefundIntegrationCollection;
