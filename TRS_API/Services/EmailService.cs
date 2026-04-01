using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using TRS_Data.Models;

namespace TRS_API.Services;

public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
        => (_config, _logger) = (config, logger);

    public async Task SendPaymentConfirmationAsync(TRSDbContext db, int registrationId, byte[] receiptPdf, CancellationToken ct = default)
    {
        var reg = await db.EventRegistrations
            .Include(r => r.Payments)
            .FirstOrDefaultAsync(r => r.RegistrationId == registrationId, ct);

        if (reg == null)
        {
            _logger.LogWarning("Unable to send confirmation email: registration {RegistrationId} not found", registrationId);
            return;
        }

        if (string.IsNullOrWhiteSpace(reg.ContactEmail))
        {
            _logger.LogWarning("Unable to send confirmation email: registration {RegistrationId} has no contact email", registrationId);
            return;
        }

        var host = _config["Email:Smtp:Host"];
        var port = _config.GetValue<int?>("Email:Smtp:Port") ?? 587;
        var username = _config["Email:Smtp:Username"];
        var password = _config["Email:Smtp:Password"];
        var fromAddress = _config["Email:FromAddress"] ?? username;
        var fromName = _config["Email:FromName"] ?? "TRS";

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
        {
            _logger.LogWarning(
                "Skipping payment confirmation email for registration {RegistrationId}: SMTP is not configured",
                registrationId);
            return;
        }

        var receiptNo = reg.Payments.OrderByDescending(p => p.CreatedAt).FirstOrDefault()?.ReceiptNumber
            ?? $"TRS-{registrationId:D6}";

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = $"TRS registration confirmed ({receiptNo})",
            Body =
                $"Hello {reg.ContactName},\n\n" +
                $"Your registration for {reg.EventName} has been confirmed.\n" +
                $"Receipt number: {receiptNo}\n\n" +
                "Your receipt is attached to this email.\n\n" +
                "Regards,\nTRS",
            IsBodyHtml = false,
        };
        message.To.Add(reg.ContactEmail);
        message.Attachments.Add(new Attachment(new MemoryStream(receiptPdf), $"Receipt-{receiptNo}.pdf", "application/pdf"));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = _config.GetValue("Email:Smtp:EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        await client.SendMailAsync(message, ct);
        _logger.LogInformation("Payment confirmation email sent for registration {RegistrationId} to {Email}", registrationId, reg.ContactEmail);
    }
}
