namespace TRS_Data.Models;
public partial class PaymentItem
{
    public int PaymentItemId { get; set; }
    public int PaymentId { get; set; }
    public int GroupId { get; set; }
    public int? ParticipantId { get; set; }    // set when FeeStructure = per_player
    public int EventId { get; set; }
    public int ProgramId { get; set; }
    public string ProgramName { get; set; } = null!;
    public string? Description { get; set; }
    public string? PlayerName { get; set; }
    public decimal Amount { get; set; }
    public string ItemStatus { get; set; } = "P";   // P|S|R
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public virtual Payment Payment { get; set; } = null!;
    public virtual ParticipantGroup Group { get; set; } = null!;
    public virtual TrsParticipant? Participant { get; set; }
    public virtual ICollection<Refund> Refunds { get; set; } = new List<Refund>();
}
