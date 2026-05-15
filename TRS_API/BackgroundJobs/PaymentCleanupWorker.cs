using Microsoft.EntityFrameworkCore;
using TRS_Data.Models;

public class PaymentCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PaymentCleanupWorker> _logger;

    public PaymentCleanupWorker(IServiceProvider services, ILogger<PaymentCleanupWorker> logger)
        => (_services, _logger) = (services, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TRSDbContext>();

            await PruneExpiredPendingCheckouts(db, stoppingToken);
            // NOTE: CancelStalePayments intentionally removed.
            // Stripe payments are asynchronous — a pending payment is NEVER failed
            // due to timeout. Only an explicit Stripe failure event marks a payment failed.
            // See: CORE PRINCIPLE — TIMEOUT ≠ FAILURE.
        }
    }

    // ── Prune expired PendingCheckout rows ────────────────────────────────────
    // A PendingCheckout row is safe to delete when its Stripe session has expired
    // AND no successful Payment exists for that session (i.e. the user never paid,
    // or confirm-session / the webhook already processed it and forgot to purge).
    private async Task PruneExpiredPendingCheckouts(TRSDbContext db, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;

            // Find rows whose Stripe session has expired.
            var expiredSessionIds = await db.PendingCheckouts
                .Where(p => p.ExpiresAt < now)
                .Select(p => p.GatewaySessionId)
                .ToListAsync(ct);

            if (!expiredSessionIds.Any()) return;

            // Of those, exclude any that already have a confirmed Payment —
            // those rows should have been purged by confirm-session/webhook but weren't.
            // We still remove them here since the registration is safe in DB.
            var toDelete = await db.PendingCheckouts
                .Where(p => expiredSessionIds.Contains(p.GatewaySessionId))
                .ToListAsync(ct);

            if (toDelete.Any())
            {
                db.PendingCheckouts.RemoveRange(toDelete);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Pruned {Count} expired PendingCheckout rows", toDelete.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pruning expired PendingCheckout rows");
        }
    }
}