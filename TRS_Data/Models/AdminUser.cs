namespace TRS_Data.Models;
public partial class AdminUser
{
    public int UserId { get; set; }
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = null!;           // superadmin | eventadmin
    public string Name { get; set; } = null!;
    public DateTime? LastLogin { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
