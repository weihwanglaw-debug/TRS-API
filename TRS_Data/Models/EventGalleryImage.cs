namespace TRS_Data.Models;
public partial class EventGalleryImage
{
    public int GalleryImageId { get; set; }
    public int EventId { get; set; }
    public string ImageUrl { get; set; } = null!;
    public int SortOrder { get; set; }
    public virtual Event Event { get; set; } = null!;
}
