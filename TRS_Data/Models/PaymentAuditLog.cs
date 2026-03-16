namespace TRS_Data.Models;
public partial class PaymentAuditLog
{
    public long AuditId { get; set; }
    public string EntityType { get; set; } = null!;   // Payment|PaymentItem|Refund
    public int EntityId { get; set; }
    public string Action { get; set; } = null!;
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? Reason { get; set; }
    public string? PerformedBy { get; set; }
    public string? IpAddress { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}
