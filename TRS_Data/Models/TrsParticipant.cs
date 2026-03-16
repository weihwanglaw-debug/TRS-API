// Named TrsParticipant — replaces the old flat Participant model
namespace TRS_Data.Models;
public partial class TrsParticipant
{
    public int ParticipantId { get; set; }
    public int GroupId { get; set; }
    public string FullName { get; set; } = null!;
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Nationality { get; set; }
    public string? ClubSchoolCompany { get; set; }
    public string? Email { get; set; }
    public string? ContactNumber { get; set; }
    public string? TshirtSize { get; set; }
    public string? SbaId { get; set; }
    public string? GuardianName { get; set; }
    public string? GuardianContact { get; set; }
    public string? DocumentUrl { get; set; }
    public string? Remark { get; set; }
    public DateTime CreatedAt { get; set; }
    public virtual ParticipantGroup Group { get; set; } = null!;
    public virtual ICollection<ParticipantCustomFieldValue> CustomFieldValues { get; set; } = new List<ParticipantCustomFieldValue>();
    public virtual ICollection<PaymentItem> PaymentItems { get; set; } = new List<PaymentItem>();
}
