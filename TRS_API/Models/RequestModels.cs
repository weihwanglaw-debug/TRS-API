using System.ComponentModel.DataAnnotations;
namespace TRS_API.Models;

// ── Auth ──────────────────────────────────────────────────────────────────────
public class LoginRequest
{
    [Required, EmailAddress] public string Email { get; set; } = null!;
    [Required] public string Password { get; set; } = null!;
}
public class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = null!;
    [Required, MinLength(8)] public string NewPassword { get; set; } = null!;
}
public class CreateUserRequest
{
    [Required, EmailAddress] public string Email { get; set; } = null!;
    [Required] public string Name { get; set; } = null!;
    public string Role { get; set; } = "eventadmin";
    [Required, MinLength(8)] public string Password { get; set; } = null!;
    public bool MustChangePassword { get; set; }
}
public class UpdateUserRequest
{
    public string? Name { get; set; }
    [EmailAddress] public string? Email { get; set; }
    public string? Role { get; set; }
}
public class ResetPasswordRequest
{
    [Required, MinLength(8)] public string NewPassword { get; set; } = null!;
}

// ── Config ────────────────────────────────────────────────────────────────────
public class UpdateConfigRequest
{
    public Dictionary<string, string> Updates { get; set; } = new();
}

// ── Events ────────────────────────────────────────────────────────────────────
public class UpsertEventRequest
{
    [Required] public string Name { get; set; } = null!;
    public string? Description { get; set; }
    [Required] public string Venue { get; set; } = null!;
    public string? VenueAddress { get; set; }
    public string? BannerUrl { get; set; }
    public string? ProspectusUrl { get; set; }
    [Required] public string EventStartDate { get; set; } = null!;
    public string? EventEndDate { get; set; }
    [Required] public string OpenDate { get; set; } = null!;
    [Required] public string CloseDate { get; set; } = null!;
    public int MaxParticipants { get; set; } = 100;
    public string? SponsorInfo { get; set; }
    public string? ConsentStatement { get; set; }
    public bool IsSports { get; set; } = true;
    public string? SportType { get; set; }
    public string FixtureMode { get; set; } = "internal";
    public List<string> GalleryUrls { get; set; } = new();
}
public class UpsertProgramRequest
{
    [Required] public string Name { get; set; } = null!;
    [Required] public string Type { get; set; } = null!;
    public int MinAge { get; set; }
    public int MaxAge { get; set; } = 99;
    [Required] public string Gender { get; set; } = null!;
    public decimal Fee { get; set; }
    public bool PaymentRequired { get; set; } = true;
    public string FeeStructure { get; set; } = "per_entry";
    public bool SbaRequired { get; set; }
    public int MinPlayers { get; set; } = 1;
    public int MaxPlayers { get; set; } = 1;
    public int MinParticipants { get; set; } = 1;
    public int MaxParticipants { get; set; } = 100;
    public ProgramFieldsDto Fields { get; set; } = new();
}
public class ProgramFieldsDto
{
    public bool EnableSbaId { get; set; }
    public bool EnableDocumentUpload { get; set; }
    public bool EnableGuardianInfo { get; set; }
    public bool EnableRemark { get; set; }
    public List<CustomFieldDto> CustomFields { get; set; } = new();
}
public class CustomFieldDto
{
    [Required] public string Label { get; set; } = null!;
    public string FieldType { get; set; } = "text";
    public bool IsRequired { get; set; }
    public string? Options { get; set; }
    public int SortOrder { get; set; }
}

// ── Registrations ─────────────────────────────────────────────────────────────
public class CreateRegistrationRequest
{
    [Required] public int EventId { get; set; }
    [Required] public string EventName { get; set; } = null!;
    [Required] public string ContactName { get; set; } = null!;
    [Required, EmailAddress] public string ContactEmail { get; set; } = null!;
    public string? ContactPhone { get; set; }
    [Required] public List<CreateGroupDto> Groups { get; set; } = new();
    [Required] public CreatePaymentDto Payment { get; set; } = null!;
}
public class CreateGroupDto
{
    [Required] public int ProgramId { get; set; }
    [Required] public string ProgramName { get; set; } = null!;
    [Required] public decimal Fee { get; set; }
    [Required] public List<CreateParticipantDto> Participants { get; set; } = new();
    public List<CreatePaymentItemDto> Items { get; set; } = new();
}
public class CreateParticipantDto
{
    [Required] public string FullName { get; set; } = null!;
    public string? Dob { get; set; }
    public string? Gender { get; set; }
    public string? Nationality { get; set; }
    public string? ClubSchoolCompany { get; set; }
    public string? Email { get; set; }
    public string? ContactNumber { get; set; }
    public string? TshirtSize { get; set; }
    public string? SbaId { get; set; }
    public string? GuardianName { get; set; }
    public string? GuardianContact { get; set; }
    public string? Remark { get; set; }
    public Dictionary<string, string> CustomFieldValues { get; set; } = new();
}
public class CreatePaymentDto
{
    public string Gateway { get; set; } = "Stripe";
    public string Method { get; set; } = "CreditCard";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "SGD";
}
public class CreatePaymentItemDto
{
    public string ProgramName { get; set; } = null!;
    public string? Description { get; set; }
    public string? PlayerName { get; set; }
    public decimal Amount { get; set; }
    public int? ParticipantIndex { get; set; }   // for per_player: index into group.Participants
}
public class UpdateRegStatusRequest
{
    [Required] public string Status { get; set; } = null!;
}
public class UpdateSeedRequest
{
    public int? Seed { get; set; }
}
public class UpdatePaymentManualRequest
{
    public string? PaymentStatus { get; set; }
    public string? Method { get; set; }
    public string? ReceiptNo { get; set; }
    public string? AdminNote { get; set; }
}
public class InitiateRefundRequest
{
    [Required] public int PaymentItemId { get; set; }
    [Required, Range(0.01, double.MaxValue)] public decimal RefundAmount { get; set; }
    public string? RefundReason { get; set; }
}

// ── Fixtures ──────────────────────────────────────────────────────────────────
public class SaveFixtureRequest
{
    [Required] public string BracketStateJson { get; set; } = null!;
    public string? FixtureFormat { get; set; }
    public string? Phase { get; set; }
    public bool IsLocked { get; set; }
}
