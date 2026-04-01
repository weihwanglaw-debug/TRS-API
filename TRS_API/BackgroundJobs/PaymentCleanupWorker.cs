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

            // Only cancel stale paid-gateway checkouts — not free registrations.
            //
            // Session-first paid registrations never write a Pending payment to the DB
            // so there is nothing to clean up for those.
            //
            // Legacy free registrations (Amount = 0) are written to the DB immediately
            // and start with PaymentStatus = "P". They stay Pending until an admin
            // manually confirms them. Cancelling them after 24 h would silently destroy
            // legitimate free-registration records, so they are explicitly excluded here.
            var expired = await db.Payments
                .Where(p => p.PaymentStatus == "P"
                         && p.Amount > 0                           // exclude free registrations
                         && p.CreatedAt < DateTime.UtcNow.AddHours(-24))
                .ToListAsync(stoppingToken);

            if (expired.Any())
            {
                var expiredRegIds = expired.Select(p => p.RegistrationId).ToList();

                var regs = await db.EventRegistrations
                    .Where(r => expiredRegIds.Contains(r.RegistrationId) && r.RegStatus == "Pending")
                    .ToListAsync(stoppingToken);

                foreach (var p in expired) { p.PaymentStatus = "X"; p.UpdatedAt = DateTime.UtcNow; }
                foreach (var r in regs)    { r.RegStatus = "Cancelled"; r.RegistrationStatus = "X"; r.UpdatedAt = DateTime.UtcNow; }

                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation(
                    "Expired {Count} stale pending paid-gateway payments → status X", expired.Count);
            }
        }
    }
}