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
    public bool EnableTshirt { get; set; }
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

public class GenerateFixtureRequest
{
    [Required] public FixtureConfigRequest Config { get; set; } = new();
    [Required] public List<FixtureSeedEntryRequest> Seeds { get; set; } = new();
    public string? PreviewBracketJson { get; set; }
}

public class FixtureConfigRequest
{
    [Required] public string Format { get; set; } = null!;
    public int NumSeeds { get; set; }
    public int? NumGroups { get; set; }
    public int? AdvancePerGroup { get; set; }
    public StandingPointsRequest? StandingPoints { get; set; }
    public HeatsConfigRequest? HeatsConfig { get; set; }
}

public class StandingPointsRequest
{
    public int Win { get; set; }
    public int Draw { get; set; }
    public int Loss { get; set; }
}

public class HeatsConfigRequest
{
    public int NumRounds { get; set; }
    public int AdvancePerRound { get; set; }
    public string ResultLabel { get; set; } = "Result";
    public int PlacesAwarded { get; set; }
}

public class FixtureSeedEntryRequest
{
    [Required] public string Id { get; set; } = null!;
    public string Club { get; set; } = "";
    public List<string> Participants { get; set; } = new();
    public int? Seed { get; set; }
    public string? SbaId { get; set; }
    public string? RegistrationId { get; set; }
    public string? GroupId { get; set; }
}

public class SwapFixtureTeamsRequest
{
    [Required] public string IdA { get; set; } = null!;
    [Required] public string IdB { get; set; } = null!;
}

public class SaveFixtureScoreRequest
{
    public List<FixtureGameScoreRequest> Games { get; set; } = new();
    public string? Winner { get; set; }
    public bool Walkover { get; set; }
    public string WalkoverWinner { get; set; } = "";
    public List<FixtureOfficialRequest> Officials { get; set; } = new();
}

public class FixtureGameScoreRequest
{
    public string P1 { get; set; } = "";
    public string P2 { get; set; } = "";
}

public class FixtureOfficialRequest
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
}

public class UpdateFixtureScheduleRequest
{
    public string CourtNo { get; set; } = "";
    public string MatchDate { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
}

public class SaveHeatResultRequest
{
    public int RoundNumber { get; set; }
    [Required] public string TeamId { get; set; } = null!;
    public string Result { get; set; } = "";
}

public class AdvanceHeatsRoundRequest
{
    public int FromRound { get; set; }
    public List<string> AdvancingIds { get; set; } = new();
}

public class AssignHeatPlacesRequest
{
    public Dictionary<string, int> Places { get; set; } = new();
}
