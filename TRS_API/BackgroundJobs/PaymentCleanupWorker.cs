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

            // Expire payments that have been Pending for >24h — status 'X' = Cancelled (not 'E')
            var expired = await db.Payments
                .Where(p => p.PaymentStatus == "P" && p.CreatedAt < DateTime.UtcNow.AddHours(-24))
                .ToListAsync(stoppingToken);

            if (expired.Any()) {
                var expiredRegIds = expired.Select(p => p.RegistrationId).ToList();

                // Also cancel the associated registrations
                var regs = await db.EventRegistrations
                    .Where(r => expiredRegIds.Contains(r.RegistrationId)
                             && r.RegStatus == "Pending")
                    .ToListAsync(stoppingToken);

                foreach (var p in expired) {
                    p.PaymentStatus = "X";
                    p.UpdatedAt = DateTime.UtcNow;
                }
                foreach (var r in regs) {
                    r.RegStatus = "Cancelled";
                    r.RegistrationStatus = "X";
                    r.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Expired {Count} stale pending payments → status X", expired.Count);
            }
        }
    }
}