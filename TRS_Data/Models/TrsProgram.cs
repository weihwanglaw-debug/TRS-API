// Named TrsProgram to avoid collision with System.Program
namespace TRS_Data.Models;
public partial class TrsProgram
{
    public int ProgramId { get; set; }
    public int EventId { get; set; }
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public int MinAge { get; set; }
    public int MaxAge { get; set; }
    public string Gender { get; set; } = null!;
    public decimal Fee { get; set; }
    public bool PaymentRequired { get; set; }
    public string FeeStructure { get; set; } = "per_entry";   // per_entry | per_player
    public bool SbaRequired { get; set; }
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int MinParticipants { get; set; }
    public int MaxParticipants { get; set; }
    public string Status { get; set; } = "open";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public virtual Event Event { get; set; } = null!;
    public virtual ProgramField? Fields { get; set; }
    public virtual ICollection<ProgramCustomField> CustomFields { get; set; } = new List<ProgramCustomField>();
    public virtual ICollection<ParticipantGroup> ParticipantGroups { get; set; } = new List<ParticipantGroup>();
}
