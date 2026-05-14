using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stripe.Checkout;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Services;

public sealed class PaymentFinalizationService
{
    private readonly TRSDbContext _db;
    private readonly RegistrationWorkflowService _registrationWorkflow;
    private readonly ILogger<PaymentFinalizationService> _log;

    public PaymentFinalizationService(
        TRSDbContext db,
        RegistrationWorkflowService registrationWorkflow,
        ILogger<PaymentFinalizationService> log)
    {
        _db = db;
        _registrationWorkflow = registrationWorkflow;
        _log = log;
    }

    public async Task<PaymentFinalizationResult> FinalizeSessionFirstAsync(
        Session session,
        CancellationToken ct = default)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.Id))
            return PaymentFinalizationResult.Fail("INVALID_SESSION", "Payment session is invalid.");

        var existing = await FindPaymentBySession(session.Id, ct);
        if (existing != null)
            return PaymentFinalizationResult.Ok(existing.RegistrationId, existing.PaymentId, alreadyProcessed: true);

        var pendingCheckout = await _db.PendingCheckouts
            .FirstOrDefaultAsync(p => p.GatewaySessionId == session.Id, ct);

        if (pendingCheckout == null)
        {
            _log.LogWarning(
                "Cannot finalize session-first checkout {SessionId}: PendingCheckout row is missing",
                session.Id);
            return PaymentFinalizationResult.Fail(
                "CHECKOUT_CONTEXT_MISSING",
                "Payment is confirmed, but registration details are still being reconciled. Please contact the organiser if you do not receive a confirmation email shortly.");
        }

        CreateRegistrationRequest? createReq;
        try
        {
            createReq = JsonSerializer.Deserialize<CreateRegistrationRequest>(
                pendingCheckout.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "PendingCheckout payload for session {SessionId} is invalid JSON", session.Id);
            return PaymentFinalizationResult.Fail("INVALID_CHECKOUT_CONTEXT", "Stored checkout details are invalid.");
        }

        if (createReq == null)
            return PaymentFinalizationResult.Fail("INVALID_CHECKOUT_CONTEXT", "Stored checkout details are invalid.");

        session.Metadata.TryGetValue("payment_method", out var paymentMethod);
        paymentMethod ??= pendingCheckout.PaymentMethod;
        if (string.IsNullOrWhiteSpace(paymentMethod)) paymentMethod = "CreditCard";

        try
        {
            var created = await _registrationWorkflow.CreateAsync(createReq, new RegistrationPersistOptions
            {
                RequireEventOpen = false,
                ValidatePricingAgainstCurrentPrograms = false,
                PaymentGateway = "Stripe",
                PaymentMethod = paymentMethod,
                PaymentStatus = "S",
                PaymentAmountOverride = (session.AmountTotal ?? 0) / 100m,
                GatewaySessionId = session.Id,
                GatewayPaymentId = session.PaymentIntentId,
            }, ct);

            if (!created.Success)
            {
                var racedPayment = await FindPaymentBySession(session.Id, ct);
                if (racedPayment != null)
                    return PaymentFinalizationResult.Ok(
                        racedPayment.RegistrationId,
                        racedPayment.PaymentId,
                        alreadyProcessed: true);

                return PaymentFinalizationResult.Fail(created.Code!, created.Message);
            }

            _db.PendingCheckouts.Remove(pendingCheckout);
            await _db.SaveChangesAsync(ct);

            return PaymentFinalizationResult.Ok(
                created.Value!.RegistrationId,
                created.Value.PaymentId,
                alreadyProcessed: false);
        }
        catch (DbUpdateException ex)
        {
            var racedPayment = await FindPaymentBySession(session.Id, ct);
            if (racedPayment != null)
            {
                _log.LogInformation(
                    ex,
                    "Session {SessionId} was finalized by a concurrent worker; returning existing registration {RegistrationId}",
                    session.Id,
                    racedPayment.RegistrationId);
                return PaymentFinalizationResult.Ok(
                    racedPayment.RegistrationId,
                    racedPayment.PaymentId,
                    alreadyProcessed: true);
            }

            _log.LogError(ex, "Database error finalizing session-first checkout {SessionId}", session.Id);
            return PaymentFinalizationResult.Fail("CREATE_FAILED", "Failed to save registration.");
        }
    }

    private Task<Payment?> FindPaymentBySession(string gatewaySessionId, CancellationToken ct) =>
        _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.GatewaySessionId == gatewaySessionId,
                ct);
}

public sealed class PaymentFinalizationResult
{
    public bool Success { get; private init; }
    public string? Code { get; private init; }
    public string Message { get; private init; } = "";
    public int RegistrationId { get; private init; }
    public int PaymentId { get; private init; }
    public bool AlreadyProcessed { get; private init; }

    public static PaymentFinalizationResult Ok(int registrationId, int paymentId, bool alreadyProcessed) => new()
    {
        Success = true,
        RegistrationId = registrationId,
        PaymentId = paymentId,
        AlreadyProcessed = alreadyProcessed,
    };

    public static PaymentFinalizationResult Fail(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}
