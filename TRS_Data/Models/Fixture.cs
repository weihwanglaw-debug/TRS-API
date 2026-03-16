namespace TRS_Data.Models;
public partial class Fixture
{
    public int FixtureId { get; set; }
    public int EventId { get; set; }
    public int ProgramId { get; set; }
    public string FixtureMode { get; set; } = "internal";
    public string? FixtureFormat { get; set; }   // knockout|group_knockout|round_robin|heats
    public bool IsLocked { get; set; }
    public string? Phase { get; set; }            // group|knockout
    public string BracketStateJson { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? GeneratedBy { get; set; }
    public virtual Event Event { get; set; } = null!;
    public virtual TrsProgram Program { get; set; } = null!;
    public virtual AdminUser? GeneratedByUser { get; set; }
}
