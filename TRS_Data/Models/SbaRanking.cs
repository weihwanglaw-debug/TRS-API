namespace TRS_Data.Models;
public partial class SbaRanking
{
    public string SbaId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Club { get; set; }
    public int AccumulatedScore { get; set; }
    public int Ranking { get; set; }
    public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateTime UpdatedAt { get; set; }
}
