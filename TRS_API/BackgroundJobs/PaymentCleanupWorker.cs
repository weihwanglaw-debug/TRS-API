using Microsoft.EntityFrameworkCore;
using TRS_Data.Models;

public class PaymentCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PaymentCleanupWorker> _logger;

    public PaymentCleanupWorker(IServiceProvider services, ILogger<PaymentCleanupWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TRSDbContext>();

            var expiredPayments = await db.Payments
                .Where(p => p.PaymentStatus == "P" && p.CreatedAt < DateTime.UtcNow.AddHours(-24))
                .ToListAsync(stoppingToken);

            foreach (var payment in expiredPayments)
            {
                payment.PaymentStatus = "E";
            }

            await db.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Expired {Count} old pending payments", expiredPayments.Count);
        }
    }
}