using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TRS_Data.Models;

namespace TRS_API.Services;

public class ReceiptService
{
    private readonly IWebHostEnvironment _env;

    public ReceiptService(IWebHostEnvironment env)
    {
        _env = env;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateAsync(TRSDbContext db, int registrationId)
    {
        // ── Load ──────────────────────────────────────────────────────────────
        var reg = await db.EventRegistrations
            .Include(r => r.ParticipantGroups).ThenInclude(g => g.Participants)
            .Include(r => r.Payments).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(r => r.RegistrationId == registrationId)
            ?? throw new KeyNotFoundException($"Registration {registrationId} not found.");

        var payment = reg.Payments.OrderByDescending(p => p.CreatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("No payment for this registration.");

        var refunds = await db.Refunds
            .Where(r => r.PaymentId == payment.PaymentId && r.RefundStatus == "S")
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        // ── Branding from SystemConfig ────────────────────────────────────────
        var configs = await db.SystemConfigs.ToListAsync();
        var cfg = configs.ToDictionary(c => c.ConfigKey, c => c.ConfigValue);
        var orgName = cfg.GetValueOrDefault("appName", "TRS");
        var orgEmail = cfg.GetValueOrDefault("contactEmail", "");
        var copyright = cfg.GetValueOrDefault("copyrightText", "");
        var logoUrl = cfg.GetValueOrDefault("logoUrl", "");
        var currency = payment.Currency;

        // ── Logo — always read as local file from wwwroot ───────────────────
        // logoUrl in SystemConfig stores the PUBLIC URL the browser uses,
        // e.g. "https://localhost:7183/images/logo.png"
        // Backend strips the host and reads the file directly from disk —
        // no HTTP call needed since the file is right here on the same server.
        byte[]? logoBytes = null;
        if (!string.IsNullOrWhiteSpace(logoUrl) && _env.WebRootPath != null)
        {
            try
            {
                // Extract just the path portion from the URL
                // "https://localhost:7183/images/logo.png" → "/images/logo.png"
                var uri = new Uri(logoUrl, UriKind.RelativeOrAbsolute);
                var filePath = uri.IsAbsoluteUri ? uri.AbsolutePath : logoUrl;

                var localPath = Path.Combine(
                    _env.WebRootPath,
                    filePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(localPath))
                    logoBytes = await File.ReadAllBytesAsync(localPath);
            }
            catch { }
        }

        // ── Totals ────────────────────────────────────────────────────────────
        var totalPaid = payment.Amount;
        var totalRefunded = refunds.Sum(r => r.RefundAmount);
        var netAmount = totalPaid - totalRefunded;

        var overallStatus = payment.PaymentStatus switch
        {
            "FR" => "FULLY REFUNDED",
            "PR" => "PARTIALLY REFUNDED",
            "S" => "PAID",
            _ => payment.PaymentStatus,
        };
        var statusColor = payment.PaymentStatus is "FR" or "PR"
            ? Colors.Orange.Darken1 : Colors.Green.Darken1;

        var methodLabel = payment.PaymentMethod switch
        {
            "CreditCard" => "Credit Card",
            "PayNow" => "PayNow",
            "Cash" => "Cash",
            "BankTransfer" => "Bank Transfer",
            _ => payment.PaymentMethod,
        };

        // ── Build lookup: GroupId → Group ─────────────────────────────────────
        var groupById = reg.ParticipantGroups
            .ToDictionary(g => g.GroupId);

        // ── Aggregate PaymentItems by Program ─────────────────────────────────
        // Groups items for the same program into one receipt row.
        // Detection: ParticipantId != null → per_player, else → per_entry
        var programLines = payment.Items
            .GroupBy(i => new { i.ProgramId, i.ProgramName })
            .Select(g =>
            {
                var items = g.ToList();
                var isPerPlayer = items.Any(i => i.ParticipantId.HasValue);

                // Total participants across all groups for this program
                var totalParticipants = items
                    .Select(i => groupById.GetValueOrDefault(i.GroupId))
                    .Where(grp => grp != null)
                    .Select(grp => grp!.Participants.Count)
                    .Sum();

                var totalAmount = items.Sum(i => i.Amount);

                int qty;
                decimal unitPrice;
                string feeType;

                if (isPerPlayer)
                {
                    // Each item = 1 player; unit price = price per player
                    qty = items.Count;
                    unitPrice = items.Count > 0 ? items[0].Amount : 0;
                    feeType = "Per Head Count";
                }
                else
                {
                    // Each item = 1 entry; qty = total players across all entries
                    // Unit price = fee per entry (same for all)
                    qty = totalParticipants > 0 ? totalParticipants : items.Count;
                    unitPrice = items.Count > 0
                        ? items[0].Amount / Math.Max(1,
                            groupById.GetValueOrDefault(items[0].GroupId)?.Participants.Count ?? 1)
                        : 0;
                    feeType = "Per Entry";
                }

                return new
                {
                    ProgramName = g.Key.ProgramName,
                    FeeType = feeType,
                    Qty = qty,
                    UnitPrice = unitPrice,
                    Amount = totalAmount,
                };
            })
            .ToList();

        // ── Aggregate Refunds by Program ──────────────────────────────────────
        // Each refund → PaymentItem → Group → Participants.Count
        // Qty is negative to show it as a deduction.
        var refundLines = refunds
            .GroupBy(r =>
            {
                var item = payment.Items.FirstOrDefault(i => i.PaymentItemId == r.PaymentItemId);
                var grp = item != null ? groupById.GetValueOrDefault(item.GroupId) : null;
                return new
                {
                    ProgramName = item?.ProgramName ?? "—",
                    ProgramId = item?.ProgramId ?? 0,
                    IsPerPlayer = item?.ParticipantId.HasValue ?? false,
                    GrpId = item?.GroupId ?? 0,
                };
            })
            .Select(g =>
            {
                var totalRefAmt = g.Sum(r => r.RefundAmount);
                var refundReason = g.Select(r => r.RefundReason)
                    .FirstOrDefault(r => !string.IsNullOrEmpty(r));
                var refundDate = g.Min(r => r.CreatedAt);
                var gatewayRefs = string.Join(", ", g
                    .Where(r => !string.IsNullOrEmpty(r.GatewayRefundId))
                    .Select(r => r.GatewayRefundId));

                // Qty = number of players returned (negative)
                int refQty;
                if (g.Key.IsPerPlayer)
                {
                    refQty = -g.Count(); // each refund = 1 player
                }
                else
                {
                    // per_entry: qty = players in the refunded group
                    var grp = groupById.GetValueOrDefault(g.Key.GrpId);
                    refQty = -(grp?.Participants.Count ?? 1);
                }

                // Unit price = refund per head
                decimal unitRef = refQty != 0
                    ? Math.Round(totalRefAmt / Math.Abs(refQty), 2)
                    : totalRefAmt;

                return new
                {
                    g.Key.ProgramName,
                    RefundDate = refundDate,
                    RefundReason = refundReason,
                    GatewayRefs = gatewayRefs,
                    Qty = refQty,
                    UnitPrice = unitRef,
                    Amount = -totalRefAmt,
                };
            })
            .ToList();

        // ── PDF ───────────────────────────────────────────────────────────────
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                page.Content().Column(col =>
                {
                    col.Spacing(0);

                    // ── HEADER ────────────────────────────────────────────────
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            if (logoBytes is { Length: > 0 })
                            {
                                c.Item().MaxHeight(56).MaxWidth(180)
                                    .Image(logoBytes).FitArea();
                                c.Item().PaddingTop(3).Text(orgName)
                                    .FontSize(10).FontColor(Colors.Grey.Darken2);
                            }
                            else
                            {
                                c.Item().Text(orgName)
                                    .FontSize(20).Bold();
                            }
                            if (!string.IsNullOrEmpty(orgEmail))
                                c.Item().PaddingTop(2).Text(orgEmail)
                                    .FontSize(9).FontColor(Colors.Grey.Darken1);
                        });

                        row.ConstantItem(145).Column(c =>
                        {
                            c.Item().AlignRight().Text("RECEIPT")
                                .FontSize(20).Bold().FontColor(statusColor);
                            c.Item().AlignRight().Text(overallStatus)
                                .FontSize(9).Bold().FontColor(statusColor);
                            c.Item().PaddingTop(6).AlignRight()
                                .Text(payment.ReceiptNumber ?? $"TRS-{reg.RegistrationId:D6}")
                                .FontSize(9).Bold().FontColor(Colors.Grey.Darken2);
                        });
                    });

                    col.Item().PaddingVertical(8)
                        .LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                    // ── REFERENCE NUMBERS ─────────────────────────────────────
                    col.Item().PaddingBottom(14).Table(t =>
                    {
                        t.ColumnsDefinition(cd => {
                            cd.RelativeColumn();
                            cd.RelativeColumn();
                            cd.RelativeColumn();
                        });
                        void Ref(string label, string value)
                        {
                            t.Cell().Column(c => {
                                c.Item().Text(label)
                                    .FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                                c.Item().PaddingTop(2).Text(value)
                                    .FontSize(10).Bold();
                            });
                        }
                        Ref("Receipt No.",
                            payment.ReceiptNumber ?? $"TRS-{reg.RegistrationId:D6}");
                        Ref("Registration No.",
                            $"TRS-{reg.RegistrationId:D6}");
                        Ref("Gateway Ref.",
                            !string.IsNullOrEmpty(payment.GatewayPaymentId)
                                ? payment.GatewayPaymentId
                                : payment.GatewaySessionId ?? "—");
                    });

                    // ── PAYMENT META (no Billed To — no dedicated contact form) ──
                    col.Item().PaddingBottom(14).Table(t =>
                    {
                        t.ColumnsDefinition(cd => {
                            cd.ConstantColumn(80); cd.RelativeColumn();
                            cd.ConstantColumn(80); cd.RelativeColumn();
                        });
                        void M(string lbl, string val)
                        {
                            t.Cell().Text(lbl).FontSize(9)
                                .FontColor(Colors.Grey.Darken1);
                            t.Cell().Text(val).FontSize(9);
                        }
                        M("Event:", reg.EventName);
                        M("Method:", methodLabel);
                        M("Date Paid:", payment.PaidAt?.ToString("dd MMM yyyy HH:mm") + " UTC" ?? "—");
                        M("Status:", overallStatus);
                    });

                    // ═══════════════════════════════════════════════════════════
                    // TABLE 1 — PAYMENT TRANSACTION
                    // ═══════════════════════════════════════════════════════════
                    col.Item().PaddingBottom(5).Row(r =>
                    {
                        r.RelativeItem().Text("PAYMENT TRANSACTION")
                            .FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                        r.AutoItem().Text(payment.PaidAt?.ToString("dd MMM yyyy") ?? "")
                            .FontSize(8).FontColor(Colors.Grey.Medium);
                    });

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.RelativeColumn(3);  // Payment Item (program name)
                            cd.ConstantColumn(36); // Qty
                            cd.ConstantColumn(80); // Type
                            cd.ConstantColumn(80); // Unit Price
                            cd.ConstantColumn(80); // Amount
                        });

                        // Header — must use table.Header() so it repeats on page breaks
                        table.Header(h =>
                        {
                            void Th(string text, bool right = false, bool center = false)
                            {
                                var cell = h.Cell()
                                    .Background(Colors.Grey.Lighten3).Padding(6);
                                var t = cell.Text(text).FontSize(9).Bold();
                                if (right) t.AlignRight();
                                if (center) t.AlignCenter();
                            }
                            Th("Payment Item");
                            Th("Qty", right: true);
                            Th("Type", center: true);
                            Th("Unit Price", right: true);
                            Th("Amount", right: true);
                        });

                        // Rows
                        foreach (var line in programLines)
                        {
                            table.Cell()
                                .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(6)
                                .Text(line.ProgramName).FontSize(9);

                            table.Cell()
                                .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(6).AlignRight()
                                .Text(line.Qty.ToString()).FontSize(9);

                            table.Cell()
                                .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(6).AlignCenter()
                                .Text(line.FeeType).FontSize(9)
                                .FontColor(Colors.Grey.Darken1);

                            table.Cell()
                                .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(6).AlignRight()
                                .Text($"{currency} {line.UnitPrice:F2}").FontSize(9);

                            table.Cell()
                                .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                .Padding(6).AlignRight()
                                .Text($"{currency} {line.Amount:F2}").FontSize(9);
                        }
                    });

                    // Subtotal row
                    col.Item().PaddingTop(6).PaddingBottom(2).AlignRight().Row(r =>
                    {
                        r.ConstantItem(160).AlignRight()
                            .Text("Subtotal").FontColor(Colors.Grey.Darken1);
                        r.ConstantItem(80).AlignRight()
                            .Text($"{currency} {totalPaid:F2}").Bold();
                    });
                    col.Item().PaddingBottom(12).AlignRight().Row(r =>
                    {
                        r.ConstantItem(160).AlignRight()
                            .Text("Payment Method").FontColor(Colors.Grey.Darken1);
                        r.ConstantItem(80).AlignRight()
                            .Text(methodLabel).Bold();
                    });

                    // ═══════════════════════════════════════════════════════════
                    // TABLE 2 — REFUND TRANSACTIONS (only when refunds exist)
                    // ═══════════════════════════════════════════════════════════
                    if (refundLines.Any())
                    {
                        col.Item().PaddingBottom(5).Row(r =>
                        {
                            r.RelativeItem().Text("REFUND TRANSACTIONS")
                                .FontSize(8).Bold().FontColor(Colors.Grey.Medium);
                        });

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cd =>
                            {
                                cd.ConstantColumn(76); // Date
                                cd.RelativeColumn(3);  // Payment Item + reason + ref
                                cd.ConstantColumn(36); // Qty
                                cd.ConstantColumn(80); // Unit Price
                                cd.ConstantColumn(80); // Amount
                            });

                            table.Header(h =>
                            {
                                void Th(string text, bool right = false)
                                {
                                    var cell = h.Cell()
                                        .Background(Colors.Orange.Lighten3).Padding(6);
                                    var t = cell.Text(text).FontSize(9).Bold();
                                    if (right) t.AlignRight();
                                }
                                Th("Date");
                                Th("Payment Item / Reference");
                                Th("Qty", right: true);
                                Th("Unit", right: true);
                                Th("Amount", right: true);
                            });

                            foreach (var r in refundLines)
                            {
                                table.Cell()
                                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Padding(6)
                                    .Text(r.RefundDate.ToString("dd MMM yyyy"))
                                    .FontSize(9).FontColor(Colors.Grey.Darken1);

                                table.Cell()
                                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Padding(6).Column(c =>
                                    {
                                        c.Item().Text(r.ProgramName).FontSize(9);
                                        if (!string.IsNullOrEmpty(r.RefundReason))
                                            c.Item().PaddingTop(1)
                                                .Text($"Reason: {r.RefundReason}")
                                                .FontSize(8).FontColor(Colors.Grey.Darken1);
                                        if (!string.IsNullOrEmpty(r.GatewayRefs))
                                            c.Item().PaddingTop(1)
                                                .Text($"Ref: {r.GatewayRefs}")
                                                .FontSize(8).FontColor(Colors.Grey.Medium);
                                    });

                                table.Cell()
                                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Padding(6).AlignRight()
                                    .Text(r.Qty.ToString()).FontSize(9)
                                    .FontColor(Colors.Orange.Darken1);

                                table.Cell()
                                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Padding(6).AlignRight()
                                    .Text($"{currency} {r.UnitPrice:F2}").FontSize(9)
                                    .FontColor(Colors.Orange.Darken1);

                                table.Cell()
                                    .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                    .Padding(6).AlignRight()
                                    .Text($"−{currency} {Math.Abs(r.Amount):F2}").FontSize(9)
                                    .FontColor(Colors.Orange.Darken1);
                            }
                        });

                        col.Item().PaddingTop(6).PaddingBottom(2).AlignRight().Row(r =>
                        {
                            r.ConstantItem(160).AlignRight()
                                .Text("Total Refunded")
                                .FontColor(Colors.Orange.Darken1);
                            r.ConstantItem(80).AlignRight()
                                .Text($"−{currency} {totalRefunded:F2}")
                                .FontColor(Colors.Orange.Darken1).Bold();
                        });
                    }

                    // ── NET TOTAL ─────────────────────────────────────────────
                    col.Item().PaddingVertical(4)
                        .LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(4).AlignRight().Row(r =>
                    {
                        r.ConstantItem(160).AlignRight()
                            .Text(refundLines.Any()
                                ? $"Net Amount ({currency})"
                                : $"Total ({currency})")
                            .Bold().FontSize(13);
                        r.ConstantItem(80).AlignRight()
                            .Text($"{currency} {netAmount:F2}")
                            .Bold().FontSize(13).FontColor(statusColor);
                    });

                    // ── FOOTER ────────────────────────────────────────────────
                    col.Item().PaddingTop(28)
                        .LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    col.Item().PaddingTop(6).Row(r =>
                    {
                        r.RelativeItem()
                            .Text(!string.IsNullOrEmpty(copyright)
                                ? copyright
                                : $"System-generated receipt — {orgName}")
                            .FontSize(8).Italic().FontColor(Colors.Grey.Medium);
                        r.ConstantItem(180).AlignRight()
                            .Text($"Generated {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC")
                            .FontSize(8).Italic().FontColor(Colors.Grey.Medium);
                    });
                    if (!string.IsNullOrEmpty(orgEmail))
                        col.Item().PaddingTop(2)
                            .Text($"Enquiries: {orgEmail}")
                            .FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf();
    }
}