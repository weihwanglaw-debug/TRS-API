namespace TRS_Data.Models;
public partial class SystemConfig
{
    public string ConfigKey { get; set; } = null!;
    public string ConfigValue { get; set; } = null!;
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
