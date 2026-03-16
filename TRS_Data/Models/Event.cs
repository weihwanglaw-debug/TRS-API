namespace TRS_Data.Models;
public partial class Event
{
    public int EventId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string Venue { get; set; } = null!;
    public string? VenueAddress { get; set; }
    public string? BannerUrl { get; set; }
    public string? ProspectusUrl { get; set; }
    public DateOnly EventStartDate { get; set; }
    public DateOnly? EventEndDate { get; set; }
    public DateOnly OpenDate { get; set; }
    public DateOnly CloseDate { get; set; }
    public int MaxParticipants { get; set; }
    public string? SponsorInfo { get; set; }
    public string? ConsentStatement { get; set; }
    public bool IsSports { get; set; }
    public string? SportType { get; set; }
    public string FixtureMode { get; set; } = "internal";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public virtual AdminUser? CreatedByUser { get; set; }
    public virtual ICollection<EventGalleryImage> GalleryImages { get; set; } = new List<EventGalleryImage>();
    public virtual ICollection<TrsProgram> Programs { get; set; } = new List<TrsProgram>();
}
