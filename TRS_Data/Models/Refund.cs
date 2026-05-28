using System;
namespace TRS_Data.Models;

public partial class Refund
{
    public int RefundId { get; set; }

    // Nullable: normal refunds have a PaymentId; Case-C orphan refunds do not
    // (money was collected by Stripe but no Registration/Payment row was ever written)
    public int? PaymentId { get; set; }

    // Nullable for same reason — orphan refunds have no PaymentItem row
    public int? PaymentItemId { get; set; }

    // Populated for orphan refunds so we can trace back to the Stripe session
    // and to the WebhookLog row that triggered the reconciliation.
    public string? GatewaySessionId { get; set; }   // e.g. cs_live_xxx
    public int?    WebhookLogId     { get; set; }   // FK → WebhookLogs.WebhookLogId

    public string PaymentGateway { get; set; } = null!;
    public string? GatewayRefundId { get; set; }
    public decimal RefundAmount { get; set; }
    public string? RefundReason { get; set; }
    public string RefundStatus { get; set; } = "P";   // P|S|F
    public string? RequestedBy { get; set; }
    public string? ApprovedBy  { get; set; }
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    // Navigation properties — nullable because orphan refunds have no parent rows
    public virtual Payment?     Payment     { get; set; }
    public virtual PaymentItem? PaymentItem { get; set; }
    public virtual WebhookLog?  WebhookLog  { get; set; }
}