using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TRS_Data;
using TRS_Data.Models;

namespace TRS_API.Controllers;

/// <summary>
/// Endpoints that serve the Payment Reconciliation page:
///   GET  /api/admin/payment-reconciliation/stats          — dashboard card count
///   GET  /api/admin/payment-reconciliation/webhook-failures — Case-C row list
///   POST /api/admin/payment-reconciliation/webhook-failures/{id}/refund — orphan refund
/// </summary>
[ApiController]
[Route("api/admin/payment-reconciliation")]
[Authorize(Roles = "superadmin,eventadmin")]
public class AdminPaymentReconciliationController : ControllerBase
{
    private readonly TRSDbContext _db;
    private readonly ILogger<AdminPaymentReconciliationController> _logger;
    private readonly IConfiguration _config;

    public AdminPaymentReconciliationController(TRSDbContext db,
        ILogger<AdminPaymentReconciliationController> logger,
        IConfiguration config)
    {
        _db     = db;
        _logger = logger;
        _config = config;
    }

    // ── GET /api/admin/payment-reconciliation/stats ───────────────────────────
    // Returns the combined count for the Dashboard "Payment Reconciliation" card.
    // caseA: RegStatus=Confirmed, PaymentStatus=P
    // caseB: RegStatus=Pending,   PaymentStatus=S
    // caseC: WebhookLog ProcessingStatus=F, EventType=checkout.session.completed,
    //        no matching Payment row (money collected, no registration)
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var caseA = await _db.EventRegistrations
            .Include(r => r.Payments)
            .CountAsync(r =>
                r.RegStatus == "Confirmed" &&
                r.Payments.Any(p => p.PaymentStatus == "P"));

        var caseB = await _db.EventRegistrations
            .Include(r => r.Payments)
            .CountAsync(r =>
                r.RegStatus == "Pending" &&
                r.Payments.Any(p => p.PaymentStatus == "S"));

        // Case C: failed checkout.session.completed webhooks where no Payment
        // row exists for that Stripe session (money collected, nothing in DB).
        var failedSessionIds = await _db.WebhookLogs
            .Where(w =>
                w.ProcessingStatus == "F" &&
                (w.EventType == "checkout.session.completed" || w.EventType == "processing_error") &&
                w.GatewaySessionId != null)
            .Select(w => w.GatewaySessionId!)
            .ToListAsync();

        var matchedSessionIds = await _db.Payments
            .Where(p => p.GatewaySessionId != null &&
                        failedSessionIds.Contains(p.GatewaySessionId!))
            .Select(p => p.GatewaySessionId!)
            .ToListAsync();

        var caseC = failedSessionIds
            .Except(matchedSessionIds)
            .Count();

        return Ok(new
        {
            caseA,
            caseB,
            caseC,
            total = caseA + caseB + caseC,
        });
    }

    // ── GET /api/admin/payment-reconciliation/webhook-failures ────────────────
    // Returns all unresolved Case-C rows for the "Unmatched Stripe Payments" tab.
    // Filters out any row where a Payment was subsequently written for that session
    // (self-healed race condition).
    [HttpGet("webhook-failures")]
    public async Task<IActionResult> GetWebhookFailures()
    {
        var failures = await _db.WebhookLogs
            .Where(w =>
                w.ProcessingStatus == "F" &&
                (w.EventType == "checkout.session.completed" || w.EventType == "processing_error") &&
                w.GatewaySessionId != null)
            .OrderByDescending(w => w.ReceivedAt)
            .ToListAsync();

        if (failures.Count == 0)
            return Ok(Array.Empty<object>());

        // Filter out self-healed rows
        var sessionIds = failures
            .Select(f => f.GatewaySessionId!)
            .ToList();

        var healedList = await _db.Payments
            .Where(p => p.GatewaySessionId != null &&
                        sessionIds.Contains(p.GatewaySessionId!))
            .Select(p => p.GatewaySessionId!)
            .ToListAsync();
        var healed = healedList.ToHashSet();

        // Also get retry counts from the log (each webhook retry creates its own row
        // with the same GatewaySessionId but a different GatewayEventId suffix —
        // Stripe re-uses the same event ID on retries, so count by GatewaySessionId).
        var retryCounts = failures
            .GroupBy(f => f.GatewaySessionId!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Deduplicate: one row per GatewaySessionId, pick the latest
        var deduped = failures
            .GroupBy(f => f.GatewaySessionId!)
            .Select(g => g.OrderByDescending(f => f.ReceivedAt).First())
            .Where(f => !healed.Contains(f.GatewaySessionId!))
            .ToList();

        var result = deduped.Select(w => new
        {
            webhookLogId   = w.WebhookLogId,
            gatewaySessionId = w.GatewaySessionId,
            errorMessage   = w.ErrorMessage,
            receivedAt     = w.ReceivedAt,
            retryCount     = retryCounts.GetValueOrDefault(w.GatewaySessionId!, 1),
            amount         = w.Amount,
            currency       = w.Currency ?? "SGD",
            contactName    = w.ContactName,
            contactEmail   = w.ContactEmail,
            contactPhone   = w.ContactPhone,
        });

        return Ok(result);
    }

    // ── POST /api/admin/payment-reconciliation/webhook-failures/{id}/refund ───
    // Issues a Stripe refund for an orphan payment (Case C).
    // Reuses the same RefundService().CreateAsync() call as the normal refund flow,
    // but writes a Refund row with PaymentId=null and marks the WebhookLog resolved.
    [HttpPost("webhook-failures/{webhookLogId:int}/refund")]
    [Authorize(Roles = "superadmin")]   // restrict to superadmin — irreversible
    public async Task<IActionResult> RefundOrphanedPayment(
        int webhookLogId,
        [FromBody] OrphanRefundRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { code = "REASON_REQUIRED", message = "Reason is required." });

        var log = await _db.WebhookLogs.FindAsync(webhookLogId);
        if (log == null)
            return NotFound(new { code = "NOT_FOUND" });

        if (log.ProcessingStatus != "F")
            return Conflict(new { code = "ALREADY_RESOLVED", message = "This webhook failure is already resolved." });

        if (string.IsNullOrEmpty(log.GatewaySessionId))
            return BadRequest(new { code = "NO_SESSION_ID", message = "Cannot refund — session ID not recorded on this webhook." });

        // Retrieve the Stripe session to get the PaymentIntent ID and amount
        StripeConfiguration.ApiKey = _config["Stripe:SecretKey"];
        Stripe.Checkout.Session? session;
        try
        {
            session = await new SessionService().GetAsync(log.GatewaySessionId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to retrieve Stripe session {SessionId}", log.GatewaySessionId);
            return StatusCode(502, new { code = "STRIPE_ERROR", message = ex.StripeError?.Message ?? ex.Message });
        }

        if (session.PaymentStatus != "paid")
            return BadRequest(new { code = "NOT_PAID", message = $"Session payment_status is '{session.PaymentStatus}' — only 'paid' sessions can be refunded here." });

        var amountCents = session.AmountTotal ?? 0;
        if (amountCents <= 0)
            return BadRequest(new { code = "ZERO_AMOUNT", message = "Session amount is zero." });

        // Write a pending Refund row before calling Stripe (safe to retry)
        TRS_Data.Models.Refund refund;
        await using (var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            log = await _db.WebhookLogs
                .FromSqlInterpolated($"SELECT * FROM WebhookLogs WITH (UPDLOCK, HOLDLOCK) WHERE WebhookLogID = {webhookLogId}")
                .SingleOrDefaultAsync();
            if (log == null)
                return NotFound(new { code = "NOT_FOUND" });
            if (log.ProcessingStatus != "F")
                return Conflict(new { code = "ALREADY_RESOLVED", message = "This webhook failure is already resolved." });

            var existing = await _db.Refunds
                .FirstOrDefaultAsync(r =>
                    r.GatewaySessionId == log.GatewaySessionId &&
                    (r.RefundStatus == "P" || r.RefundStatus == "S"));

            if (existing?.RefundStatus == "S")
                return Conflict(new
                {
                    code = "ALREADY_REFUNDED",
                    message = "A successful refund already exists for this session.",
                    gatewayRefundId = existing.GatewayRefundId,
                });

            if (existing != null)
            {
                if (!string.IsNullOrEmpty(existing.GatewayRefundId))
                    return Conflict(new { code = "REFUND_IN_PROGRESS", message = "A refund is already in progress for this session." });
                refund = existing;
            }
            else
            {
                refund = new TRS_Data.Models.Refund
        {
            PaymentId       = null,           // orphan — no Payment row
            PaymentItemId   = null,           // orphan — no PaymentItem row
            GatewaySessionId = log.GatewaySessionId,
            WebhookLogId    = webhookLogId,
            PaymentGateway  = "Stripe",
            RefundAmount    = amountCents / 100m,
            RefundReason    = req.Reason,
            RefundStatus    = "P",
            RequestedBy     = User.Identity?.Name ?? "admin",
            CreatedAt       = DateTime.UtcNow,
        };
                _db.Refunds.Add(refund);
                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogWarning(ex, "Duplicate orphan refund prevented for session {SessionId}", log.GatewaySessionId);
                    return Conflict(new { code = "REFUND_IN_PROGRESS", message = "A refund is already in progress for this session." });
                }
            }

            await tx.CommitAsync();
        }

        try
        {
            // Same call pattern as ProcessRefundItemAsync in RegistrationsController
            var stripeRefund = await new RefundService().CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = session.PaymentIntentId,
                    Amount        = amountCents,
                    Reason        = "requested_by_customer",
                    Metadata      = new Dictionary<string, string>
                    {
                        ["webhook_log_id"] = webhookLogId.ToString(),
                        ["admin_note"]     = req.AdminNote ?? "",
                    },
                },
                new RequestOptions { IdempotencyKey = $"orphan_refund_{log.GatewaySessionId}" });

            refund.GatewayRefundId = stripeRefund.Id;
            refund.RefundStatus    = stripeRefund.Status == "failed" ? "F" : "S";
            refund.ProcessedAt     = DateTime.UtcNow;

            if (refund.RefundStatus == "S")
            {
                // Mark the WebhookLog resolved so it drops off the Case-C list
                log.ProcessingStatus = "S";
                log.ProcessedAt      = DateTime.UtcNow;
            }

            _db.PaymentAuditLogs.Add(new PaymentAuditLog
            {
                EntityType  = "OrphanRefund",
                EntityId    = refund.RefundId,
                Action      = refund.RefundStatus == "S" ? "OrphanRefundIssued" : "OrphanRefundFailed",
                Reason      = req.Reason,
                PerformedBy = User.Identity?.Name ?? "admin",
                Notes       = $"WebhookLogId={webhookLogId} SessionId={log.GatewaySessionId} " +
                              $"Payer={log.ContactEmail} AdminNote={req.AdminNote}",
                CreatedAt   = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();

            if (refund.RefundStatus != "S")
                return StatusCode(502, new { code = "REFUND_FAILED", message = "Stripe accepted the request but the refund did not complete." });

            return Ok(new
            {
                refundId        = refund.RefundId,
                refundStatus    = refund.RefundStatus,
                refundAmount    = refund.RefundAmount,
                gatewayRefundId = refund.GatewayRefundId,
            });
        }
        catch (StripeException ex)
        {
            refund.RefundStatus = "F";
            refund.ProcessedAt  = DateTime.UtcNow;

            _db.PaymentAuditLogs.Add(new PaymentAuditLog
            {
                EntityType  = "OrphanRefund",
                EntityId    = refund.RefundId,
                Action      = "OrphanRefundFailed",
                Reason      = req.Reason,
                PerformedBy = User.Identity?.Name ?? "admin",
                Notes       = ex.StripeError?.Message ?? ex.Message,
                CreatedAt   = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();

            _logger.LogError(ex, "Stripe refund failed for orphan session {SessionId}", log.GatewaySessionId);
            return StatusCode(502, new { code = ex.StripeError?.Code ?? "REFUND_FAILED", message = ex.StripeError?.Message ?? ex.Message });
        }
    }
}

// ── Request model ─────────────────────────────────────────────────────────────
public class OrphanRefundRequest
{
    public string  Reason    { get; set; } = null!;
    public string? AdminNote { get; set; }
}
