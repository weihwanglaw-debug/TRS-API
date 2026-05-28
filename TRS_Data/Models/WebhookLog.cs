using System;

namespace TRS_Data.Models;

public partial class WebhookLog
{
    public int WebhookLogId { get; set; }

    public int? PaymentId { get; set; }       // FK → Payments — nullable (set after payment matched)

    public string PaymentGateway { get; set; } = null!;

    public string GatewayEventId { get; set; } = null!;

    // Stripe session ID — denormalised from metadata so Case-C queries don't need
    // to deserialise PayloadJson just to match a session.
    public string? GatewaySessionId { get; set; }

    public string EventType { get; set; } = null!;

    public string PayloadJson { get; set; } = null!;

    public string ProcessingStatus { get; set; } = "P";   // P|S|F|I

    public string? ErrorMessage { get; set; }

    public DateTime  ReceivedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    // ── Payer contact — populated from Stripe session metadata at webhook receipt time.
    // Stored here so the admin payment-reconciliation page can display contact info
    // for Case-C rows (no Registration/Payment row exists) without a live Stripe API call.
    public string?  ContactName  { get; set; }
    public string?  ContactEmail { get; set; }
    public string?  ContactPhone { get; set; }
    public decimal? Amount       { get; set; }
    public string?  Currency     { get; set; }
}