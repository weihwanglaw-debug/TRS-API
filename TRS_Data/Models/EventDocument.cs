namespace TRS_Data.Models;

public partial class EventDocument
{
    public int    EventDocumentId { get; set; }
    public int    EventId         { get; set; }
    public string Label           { get; set; } = null!;   // e.g. "Prospectus", "Visa Form"
    public string FileUrl         { get; set; } = null!;
    public int    DisplayOrder    { get; set; }
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;

    public virtual Event Event { get; set; } = null!;
}