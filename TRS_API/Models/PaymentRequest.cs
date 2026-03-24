using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace TRS_API.Models
{
    // ── Existing flow: free registrations (registrationId already in DB) ──────
    // ── New flow: paid registrations send registrationPayload instead ─────────
    public class PaymentRequest
    {
        // Legacy path (free registrations): reg already in DB
        public int RegistrationId { get; set; }

        // Session-first path (paid registrations): full cart payload, no DB record yet
        public JsonElement? RegistrationPayload { get; set; }

        public string? PaymentMethod { get; set; }
        public string? SuccessUrl { get; set; }
        public string? CancelUrl { get; set; }

        // True when using session-first paid flow
        public bool IsSessionFirst => RegistrationPayload.HasValue && RegistrationId <= 0;
    }

    // ── New: confirm-session request (called by PaymentResult on success) ─────
    public class ConfirmSessionRequest
    {
        [Required] public string GatewaySessionId { get; set; } = null!;
        [Required] public JsonElement RegistrationPayload { get; set; }
    }
}