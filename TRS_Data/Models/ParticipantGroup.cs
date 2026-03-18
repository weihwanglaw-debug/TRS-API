namespace TRS_Data.Models;
public partial class ParticipantGroup
{
    public int GroupId { get; set; }
    public int RegistrationId { get; set; }
    public int EventId { get; set; }
    public int ProgramId { get; set; }
    public string ProgramName { get; set; } = null!;   // snapshotted at checkout
    public decimal Fee { get; set; }
    public string GroupStatus { get; set; } = "Pending";  // Pending|Confirmed|Cancelled|Waitlisted
    public int? Seed { get; set; }
    public string? ClubDisplay { get; set; }
    public string? NamesDisplay { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public virtual EventRegistration Registration { get; set; } = null!;
    public virtual Event Event { get; set; } = null!;
    public virtual TrsProgram Program { get; set; } = null!;
    public virtual ICollection<Participant> Participants { get; set; } = new List<Participant>();
    public virtual ICollection<PaymentItem> PaymentItems { get; set; } = new List<PaymentItem>();
}