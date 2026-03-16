namespace TRS_Data.Models;
public partial class ParticipantCustomFieldValue
{
    public int ValueId { get; set; }
    public int ParticipantId { get; set; }
    public int CustomFieldId { get; set; }
    public string FieldLabel { get; set; } = null!;    // snapshotted label
    public string? FieldValue { get; set; }
    public virtual TrsParticipant Participant { get; set; } = null!;
    public virtual ProgramCustomField CustomField { get; set; } = null!;
}
