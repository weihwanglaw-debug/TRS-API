namespace TRS_Data.Models;
public partial class AdminAuditLog
{
    public long AuditId { get; set; }
    public int? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = null!;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? IpAddress { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public virtual AdminUser? User { get; set; }
}
