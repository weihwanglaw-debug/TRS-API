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
            await CancelStalePayments(db, stoppingToken);
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

    // ── Cancel stale pending paid-gateway payments ────────────────────────────
    // Only cancels legacy paid registrations (Amount > 0, PaymentStatus = "P")
    // that have been pending for more than 24 hours.
    //
    // Session-first paid registrations never write a Pending payment to the DB,
    // so there is nothing to cancel for those.
    //
    // Free registrations (Amount = 0) stay Pending until an admin manually
    // confirms them — they are explicitly excluded here.
    private async Task CancelStalePayments(TRSDbContext db, CancellationToken ct)
    {
        try
        {
            var expired = await db.Payments
                .Where(p => p.PaymentStatus == "P"
                         && p.Amount > 0                           // exclude free registrations
                         && p.CreatedAt < DateTime.UtcNow.AddHours(-24))
                .ToListAsync(ct);

            if (!expired.Any()) return;

            var expiredRegIds = expired.Select(p => p.RegistrationId).ToList();

            var regs = await db.EventRegistrations
                .Where(r => expiredRegIds.Contains(r.RegistrationId) && r.RegStatus == "Pending")
                .ToListAsync(ct);

            foreach (var p in expired) { p.PaymentStatus = "X"; p.UpdatedAt = DateTime.UtcNow; }
            foreach (var r in regs)    { r.RegStatus = "Cancelled"; r.RegistrationStatus = "X"; r.UpdatedAt = DateTime.UtcNow; }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Expired {Count} stale pending paid-gateway payments → status X", expired.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling stale pending payments");
        }
    }
}