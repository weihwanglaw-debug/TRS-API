namespace TRS_Data.Models;

/// <summary>
/// Server-side ledger for session-first paid checkouts.
///
/// Written by CreateSessionFirstCheckout() immediately after the Stripe session
/// is created — BEFORE the user is redirected to Stripe. Holds the full
/// registration payload JSON so the Stripe webhook can reconstruct and insert
/// the registration even if the user never returns to /payment/result.
///
/// Lifecycle:
///   Created  → CreateSessionFirstCheckout() writes the row
///   Purged   → confirm-session() deletes it after a successful DB insert
///   Purged   → HandleCheckoutCompleted() (webhook) deletes it after DB insert
///   Purged   → PaymentCleanupWorker deletes it after ExpiresAt passes
///              (session expired, user never paid — nothing to save)
/// </summary>
public class PendingCheckout
{
    /// <summary>Stripe checkout session ID — e.g. cs_live_abc123</summary>
    public string GatewaySessionId { get; set; } = null!;

    public int    EventId        { get; set; }
    public string ContactEmail   { get; set; } = null!;

    /// <summary>Full buildRegistrationPayload() JSON from the frontend.</summary>
    public string PayloadJson    { get; set; } = null!;

    public string PaymentMethod  { get; set; } = "CreditCard";

    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Mirrors the Stripe session ExpiresAt (30 min for PayNow, 24 h for card).
    /// Used by PaymentCleanupWorker to prune rows for sessions that were never paid.
    /// </summary>
    public DateTime ExpiresAt    { get; set; }
}