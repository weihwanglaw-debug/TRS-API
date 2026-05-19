using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Services;

public class RegistrationWorkflowService
{
    private readonly TRSDbContext _db;
    private readonly ILogger<RegistrationWorkflowService> _log;
    private readonly IBackgroundJobQueue _jobQueue;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public RegistrationWorkflowService(
        TRSDbContext db,
        ILogger<RegistrationWorkflowService> log,
        IBackgroundJobQueue jobQueue,
        IServiceScopeFactory serviceScopeFactory)
    {
        _db = db;
        _log = log;
        _jobQueue = jobQueue;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<RegistrationWorkflowResult<PricingQuote>> ValidateAndPriceAsync(
        CreateRegistrationRequest req,
        RegistrationValidationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RegistrationValidationOptions();

        var eventEntity = await _db.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == req.EventId, ct);

        if (eventEntity == null || !eventEntity.IsActive)
            return RegistrationWorkflowResult<PricingQuote>.Fail("EVENT_NOT_FOUND", "Event not found.");

        if (options.RequireEventOpen && !IsEventOpen(eventEntity))
            return RegistrationWorkflowResult<PricingQuote>.Fail("EVENT_CLOSED", "This event is not accepting registrations.");

        if (req.Groups == null || req.Groups.Count == 0)
            return RegistrationWorkflowResult<PricingQuote>.Fail("INVALID_REGISTRATION", "At least one program is required.");

        var programIds = req.Groups.Select(g => g.ProgramId).Distinct().ToList();
        var programs = await _db.Programs
            .Include(p => p.Fields)
            .Include(p => p.CustomFields)
            .Where(p => p.EventId == req.EventId && programIds.Contains(p.ProgramId))
            .ToDictionaryAsync(p => p.ProgramId, ct);

        if (programIds.Any(id => !programs.ContainsKey(id)))
            return RegistrationWorkflowResult<PricingQuote>.Fail("PROGRAM_NOT_FOUND", "One or more selected programs could not be found.");

        var activeCounts = await _db.ParticipantGroups
            .Where(g => programIds.Contains(g.ProgramId) && g.GroupStatus != "Cancelled")
            .GroupBy(g => g.ProgramId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var existingParticipants = await _db.ParticipantGroups
            .Where(g => programIds.Contains(g.ProgramId) && g.GroupStatus != "Cancelled")
            .SelectMany(g => g.Participants.Select(p => new ExistingParticipantIdentity
            {
                ProgramId = g.ProgramId,
                FullName = p.FullName,
                DateOfBirth = p.DateOfBirth,
            }))
            .ToListAsync(ct);

        var requestedPerProgram = req.Groups
            .GroupBy(g => g.ProgramId)
            .ToDictionary(g => g.Key, g => g.Count());

        var quoteGroups = new List<PricingQuoteGroup>();

        foreach (var group in req.Groups)
        {
            var program = programs[group.ProgramId];

            if (!program.IsActive || string.Equals(program.Status, "closed", StringComparison.OrdinalIgnoreCase))
                return RegistrationWorkflowResult<PricingQuote>.Fail("PROGRAM_CLOSED", $"'{program.Name}' is no longer accepting registrations.");

            var activeCount = activeCounts.GetValueOrDefault(group.ProgramId);
            var requestedCount = requestedPerProgram[group.ProgramId];
            if (activeCount + requestedCount > program.MaxParticipants)
                return RegistrationWorkflowResult<PricingQuote>.Fail("PROGRAM_FULL", $"'{program.Name}' does not have enough remaining slots.");

            var participantValidation = ValidateParticipants(group, program, existingParticipants);
            if (!participantValidation.Success)
                return RegistrationWorkflowResult<PricingQuote>.Fail(participantValidation.Code!, participantValidation.Message);

            var expectedFee = program.PaymentRequired
                ? program.Fee * (string.Equals(program.FeeStructure, "per_player", StringComparison.OrdinalIgnoreCase)
                    ? group.Participants.Count
                    : 1)
                : 0m;

            if (options.ValidatePricingAgainstCurrentPrograms && group.Fee != expectedFee)
                return RegistrationWorkflowResult<PricingQuote>.Fail("PRICE_MISMATCH", $"'{program.Name}' pricing no longer matches the latest configuration.");

            quoteGroups.Add(new PricingQuoteGroup
            {
                ProgramId = group.ProgramId,
                ProgramName = program.Name,
                ExpectedFee = expectedFee,
                PaymentRequired = program.PaymentRequired,
                FeeStructure = program.FeeStructure,
                ParticipantCount = group.Participants.Count,
            });
        }

        var expectedTotal = quoteGroups.Sum(g => g.ExpectedFee);
        if (options.ValidatePricingAgainstCurrentPrograms && req.Payment.Amount != expectedTotal)
            return RegistrationWorkflowResult<PricingQuote>.Fail("PRICE_MISMATCH", "Registration total no longer matches the latest pricing.");

        return RegistrationWorkflowResult<PricingQuote>.Ok(new PricingQuote
        {
            EventId = eventEntity.EventId,
            EventName = eventEntity.Name,
            Currency = string.IsNullOrWhiteSpace(req.Payment.Currency) ? "SGD" : req.Payment.Currency,
            TotalAmount = expectedTotal,
            Groups = quoteGroups,
        });
    }

    public async Task<RegistrationWorkflowResult<RegistrationCreateOutcome>> CreateAsync(
        CreateRegistrationRequest req,
        RegistrationPersistOptions options,
        CancellationToken ct = default)
    {
        var pricing = await ValidateAndPriceAsync(req, new RegistrationValidationOptions
        {
            RequireEventOpen = options.RequireEventOpen,
            ValidatePricingAgainstCurrentPrograms = options.ValidatePricingAgainstCurrentPrograms,
        }, ct);

        if (!pricing.Success)
            return RegistrationWorkflowResult<RegistrationCreateOutcome>.Fail(pricing.Code!, pricing.Message);

        using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            var programIds = req.Groups.Select(g => g.ProgramId).Distinct().ToList();
            var customFieldsByProgram = await _db.ProgramCustomFields
                .Where(cf => programIds.Contains(cf.ProgramId))
                .GroupBy(cf => cf.ProgramId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.ToDictionary(cf => cf.Label, cf => cf.CustomFieldId),
                    ct);

            var programs = await _db.Programs
                .Include(p => p.Fields)
                .Include(p => p.CustomFields)
                .Where(p => p.EventId == req.EventId && programIds.Contains(p.ProgramId))
                .ToDictionaryAsync(p => p.ProgramId, ct);

            var reg = new EventRegistration
            {
                EventId = pricing.Value!.EventId,
                EventName = pricing.Value.EventName,
                RegStatus = options.PaymentStatus == "S" ? "Confirmed" : "Pending",
                ContactName = req.ContactName,
                ContactEmail = req.ContactEmail,
                ContactPhone = req.ContactPhone,
                SubmittedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                TotalAmount = options.PaymentAmountOverride ?? pricing.Value.TotalAmount,
                Currency = pricing.Value.Currency,
                RegistrationStatus = options.PaymentStatus == "S" ? "C" : "P",
                ConfirmedAt = options.PaymentStatus == "S" ? DateTime.UtcNow : null,
            };
            _db.EventRegistrations.Add(reg);
            await _db.SaveChangesAsync(ct);

            var createdGroups = new List<ParticipantGroup>();
            var pendingItems = new List<PendingPaymentItem>();

            foreach (var groupDto in req.Groups)
            {
                var program = await _db.Programs
                    .FromSqlRaw("SELECT * FROM Programs WITH (UPDLOCK, ROWLOCK) WHERE ProgramID = {0}", groupDto.ProgramId)
                    .FirstOrDefaultAsync(ct);

                if (program == null || program.EventId != req.EventId)
                    return await RollbackAndFail(tx, "PROGRAM_NOT_FOUND", "One or more selected programs could not be found.");

                if (!program.IsActive || string.Equals(program.Status, "closed", StringComparison.OrdinalIgnoreCase))
                    return await RollbackAndFail(tx, "PROGRAM_CLOSED", $"'{program.Name}' is no longer accepting registrations.");

                var activeGroupCount = await _db.ParticipantGroups
                    .CountAsync(g => g.ProgramId == groupDto.ProgramId && g.GroupStatus != "Cancelled", ct);

                if (activeGroupCount >= program.MaxParticipants)
                    return await RollbackAndFail(tx, "PROGRAM_FULL", $"'{program.Name}' is full. No slots remaining.");

                var duplicateCheck = await FindDuplicateAsync(groupDto, groupDto.ProgramId, ct);
                if (duplicateCheck)
                    return await RollbackAndFail(tx, "DUPLICATE_REGISTRATION", $"One or more participants are already registered for '{program.Name}'.");

                var persistedGroupFee = options.ValidatePricingAgainstCurrentPrograms
                    ? pricing.Value.Groups.First(g => g.ProgramId == groupDto.ProgramId).ExpectedFee
                    : groupDto.Fee;

                var group = new ParticipantGroup
                {
                    RegistrationId = reg.RegistrationId,
                    EventId = req.EventId,
                    ProgramId = groupDto.ProgramId,
                    ProgramName = program.Name,
                    Fee = persistedGroupFee,
                    GroupStatus = options.PaymentStatus == "S" ? "Confirmed" : "Pending",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = options.PaymentStatus == "S" ? DateTime.UtcNow : null,
                };
                _db.ParticipantGroups.Add(group);
                await _db.SaveChangesAsync(ct);

                var createdParticipants = new List<Participant>();
                foreach (var participantDto in groupDto.Participants)
                {
                    var participant = new Participant
                    {
                        GroupId = group.GroupId,
                        FullName = participantDto.FullName,
                        DateOfBirth = ParseDob(participantDto.Dob),
                        Gender = participantDto.Gender,
                        Nationality = participantDto.Nationality,
                        ClubSchoolCompany = participantDto.ClubSchoolCompany,
                        Email = participantDto.Email,
                        ContactNumber = participantDto.ContactNumber,
                        TshirtSize = participantDto.TshirtSize,
                        SbaId = participantDto.SbaId,
                        GuardianName = participantDto.GuardianName,
                        GuardianContact = participantDto.GuardianContact,
                        DocumentUrl = participantDto.DocumentUrl,
                        Remark = participantDto.Remark,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _db.Participants.Add(participant);
                    createdParticipants.Add(participant);
                }
                await _db.SaveChangesAsync(ct);

                var customFieldLookup = customFieldsByProgram.GetValueOrDefault(groupDto.ProgramId)
                    ?? new Dictionary<string, int>();

                for (var pi = 0; pi < groupDto.Participants.Count; pi++)
                {
                    foreach (var (label, value) in groupDto.Participants[pi].CustomFieldValues)
                    {
                        if (!customFieldLookup.TryGetValue(label, out var customFieldId))
                        {
                            _log.LogWarning("Custom field label '{Label}' not found for program {ProgramId} - skipping", label, groupDto.ProgramId);
                            continue;
                        }

                        _db.ParticipantCustomFieldValues.Add(new ParticipantCustomFieldValue
                        {
                            ParticipantId = createdParticipants[pi].ParticipantId,
                            CustomFieldId = customFieldId,
                            FieldLabel = label,
                            FieldValue = value,
                        });
                    }
                }

                group.ClubDisplay = createdParticipants.FirstOrDefault()?.ClubSchoolCompany ?? "";
                group.NamesDisplay = string.Join(" / ", createdParticipants.Select(p => p.FullName));

                if (string.Equals(program.FeeStructure, "per_player", StringComparison.OrdinalIgnoreCase))
                {
                    for (var pi = 0; pi < createdParticipants.Count; pi++)
                    {
                        pendingItems.Add(new PendingPaymentItem
                        {
                            GroupId = group.GroupId,
                            EventId = req.EventId,
                            ProgramId = groupDto.ProgramId,
                            ProgramName = program.Name,
                            Description = $"{program.Name} - {createdParticipants[pi].FullName}",
                            PlayerName = createdParticipants[pi].FullName,
                            Amount = options.ValidatePricingAgainstCurrentPrograms
                                ? (program.PaymentRequired ? program.Fee : 0m)
                                : ResolveSnapshotPerPlayerAmount(groupDto),
                            ParticipantId = createdParticipants[pi].ParticipantId,
                        });
                    }
                }
                else
                {
                    pendingItems.Add(new PendingPaymentItem
                    {
                        GroupId = group.GroupId,
                        EventId = req.EventId,
                        ProgramId = groupDto.ProgramId,
                        ProgramName = program.Name,
                        Description = $"{program.Name} - {string.Join(" / ", createdParticipants.Select(p => p.FullName))}",
                        Amount = persistedGroupFee,
                    });
                }

                createdGroups.Add(group);
            }

            var isConfirmed = options.PaymentStatus == "S";
            var receiptNo = isConfirmed
                ? $"TRS-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999):D5}"
                : null;

            var payment = new Payment
            {
                RegistrationId = reg.RegistrationId,
                EventId = req.EventId,
                PaymentGateway = options.PaymentGateway,
                PaymentMethod = options.PaymentMethod,
                Amount = options.PaymentAmountOverride ?? pricing.Value.TotalAmount,
                Currency = pricing.Value.Currency,
                PaymentStatus = options.PaymentStatus,
                GatewaySessionId = options.GatewaySessionId,
                GatewayPaymentId = options.GatewayPaymentId,
                ReceiptNumber = receiptNo,
                CreatedAt = DateTime.UtcNow,
                PaidAt = isConfirmed ? DateTime.UtcNow : null,
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync(ct);

            foreach (var item in pendingItems)
            {
                _db.PaymentItems.Add(new PaymentItem
                {
                    PaymentId = payment.PaymentId,
                    GroupId = item.GroupId,
                    EventId = item.EventId,
                    ProgramId = item.ProgramId,
                    ProgramName = item.ProgramName,
                    Description = item.Description,
                    PlayerName = item.PlayerName,
                    Amount = item.Amount,
                    ItemStatus = isConfirmed ? "S" : "P",
                    CreatedAt = DateTime.UtcNow,
                    ParticipantId = item.ParticipantId,
                });
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (isConfirmed)
                await QueueReceiptEmailAsync(reg.RegistrationId, payment.PaymentId);

            return RegistrationWorkflowResult<RegistrationCreateOutcome>.Ok(new RegistrationCreateOutcome
            {
                RegistrationId = reg.RegistrationId,
                PaymentId = payment.PaymentId,
                TotalAmount = payment.Amount,
                PaymentStatus = payment.PaymentStatus,
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _log.LogError(ex, "Error creating registration for event {EventId}", req.EventId);
            return RegistrationWorkflowResult<RegistrationCreateOutcome>.Fail("CREATE_FAILED", "Failed to save registration.");
        }
    }

    private async Task<bool> FindDuplicateAsync(CreateGroupDto group, int programId, CancellationToken ct)
    {

               // Step 1: Server-side — EF translates these two predicates fine
        var existingParticipants = await _db.ParticipantGroups
            .Where(g =>
                g.ProgramId == programId &&
                g.GroupStatus != "Cancelled")
            .SelectMany(g => g.Participants.Select(p => new
            {
                p.FullName,
                p.DateOfBirth
            }))
            .ToListAsync(ct);

        // Step 2: Client-side — plain LINQ against two in-memory lists
        var incoming = group.Participants
            .Select(p => new
            {
                p.FullName,
                Dob = ParseDob(p.Dob)
            })
            .ToList();
    
        return existingParticipants.Any(existing =>
            incoming.Any(i =>
                string.Equals(i.FullName, existing.FullName, StringComparison.OrdinalIgnoreCase)
                && i.Dob == existing.DateOfBirth));

    }

    private RegistrationWorkflowResult<object> ValidateParticipants(
        CreateGroupDto group,
        TrsProgram program,
        List<ExistingParticipantIdentity> existingParticipants)
    {
        if (group.Participants == null || group.Participants.Count < program.MinPlayers || group.Participants.Count > program.MaxPlayers)
            return RegistrationWorkflowResult<object>.Fail("INVALID_PARTICIPANT_COUNT", $"'{program.Name}' requires between {program.MinPlayers} and {program.MaxPlayers} participants.");

        var participantIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var participant in group.Participants)
        {
            if (string.IsNullOrWhiteSpace(participant.FullName))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", "Participant full name is required.");
            if (string.IsNullOrWhiteSpace(participant.Dob) || ParseDob(participant.Dob) == null)
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Date of birth is required for '{participant.FullName}'.");
            if (string.IsNullOrWhiteSpace(participant.Gender))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Gender is required for '{participant.FullName}'.");
            if (string.IsNullOrWhiteSpace(participant.Email))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Email is required for '{participant.FullName}'.");
            if (string.IsNullOrWhiteSpace(participant.ContactNumber))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Contact number is required for '{participant.FullName}'.");
            if (string.IsNullOrWhiteSpace(participant.Nationality))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Nationality is required for '{participant.FullName}'.");
            if (string.IsNullOrWhiteSpace(participant.ClubSchoolCompany))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Club / school / company is required for '{participant.FullName}'.");

            var dob = ParseDob(participant.Dob)!;
            var identity = $"{participant.FullName}|{dob:yyyy-MM-dd}";
            if (!participantIdentities.Add(identity))
                return RegistrationWorkflowResult<object>.Fail("DUPLICATE_REGISTRATION", $"Duplicate participant detected in '{program.Name}'.");

            var age = CalculateAge(dob.Value);
            if (age < program.MinAge || age > program.MaxAge)
                return RegistrationWorkflowResult<object>.Fail("INVALID_AGE", $"'{participant.FullName}' does not meet the age requirement for '{program.Name}'.");

            if (string.Equals(program.Gender, "Male", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(participant.Gender, "Male", StringComparison.OrdinalIgnoreCase))
                return RegistrationWorkflowResult<object>.Fail("INVALID_GENDER", $"'{program.Name}' is for male participants only.");

            if (string.Equals(program.Gender, "Female", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(participant.Gender, "Female", StringComparison.OrdinalIgnoreCase))
                return RegistrationWorkflowResult<object>.Fail("INVALID_GENDER", $"'{program.Name}' is for female participants only.");

            if (program.Fields?.EnableTshirt == true && string.IsNullOrWhiteSpace(participant.TshirtSize))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"T-shirt size is required for '{participant.FullName}'.");

            if (program.Fields?.EnableGuardianInfo == true)
            {
                if (string.IsNullOrWhiteSpace(participant.GuardianName) || string.IsNullOrWhiteSpace(participant.GuardianContact))
                    return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"Guardian details are required for '{participant.FullName}'.");
            }

            if (program.Fields?.EnableSbaId == true && program.SbaRequired && string.IsNullOrWhiteSpace(participant.SbaId))
                return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"SBA ID is required for '{participant.FullName}'.");

            foreach (var customField in program.CustomFields.Where(cf => cf.IsRequired))
            {
                if (!participant.CustomFieldValues.TryGetValue(customField.Label, out var value) || string.IsNullOrWhiteSpace(value))
                    return RegistrationWorkflowResult<object>.Fail("MISSING_REQUIRED_FIELD", $"'{customField.Label}' is required for '{participant.FullName}'.");
            }

            if (existingParticipants.Any(existing =>
                existing.ProgramId == group.ProgramId
                && string.Equals(existing.FullName, participant.FullName, StringComparison.OrdinalIgnoreCase)
                && existing.DateOfBirth == dob))
            {
                return RegistrationWorkflowResult<object>.Fail("DUPLICATE_REGISTRATION", $"One or more participants are already registered for '{program.Name}'.");
            }
        }

        if (string.Equals(program.Gender, "Mixed", StringComparison.OrdinalIgnoreCase))
        {
            var maleCount = group.Participants.Count(p => string.Equals(p.Gender, "Male", StringComparison.OrdinalIgnoreCase));
            var femaleCount = group.Participants.Count(p => string.Equals(p.Gender, "Female", StringComparison.OrdinalIgnoreCase));
            if (maleCount != 1 || femaleCount != 1)
                return RegistrationWorkflowResult<object>.Fail("INVALID_GENDER", $"'{program.Name}' requires exactly one male and one female participant.");
        }

        return RegistrationWorkflowResult<object>.Ok(null);
    }

    private static bool IsEventOpen(Event eventEntity)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return eventEntity.IsActive
            && eventEntity.OpenDate <= today
            && eventEntity.CloseDate >= today;
    }

    private static DateOnly? ParseDob(string? dob)
    {
        if (string.IsNullOrWhiteSpace(dob))
            return null;

        return DateOnly.TryParse(dob, out var parsed) ? parsed : null;
    }

    private static int CalculateAge(DateOnly dob)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age))
            age--;
        return age;
    }

    private static decimal ResolveSnapshotPerPlayerAmount(CreateGroupDto group)
    {
        if (group.Participants.Count == 0)
            return 0m;
        return decimal.Round(group.Fee / group.Participants.Count, 2);
    }

    private async Task QueueReceiptEmailAsync(int registrationId, int paymentId)
    {
        await _jobQueue.EnqueueAsync(async ct =>
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var receiptSvc = scope.ServiceProvider.GetRequiredService<ReceiptService>();
            var emailSvc = scope.ServiceProvider.GetRequiredService<EmailService>();
            var jobDb = scope.ServiceProvider.GetRequiredService<TRSDbContext>();

            try
            {
                var pdfBytes = await receiptSvc.GenerateAsync(jobDb, registrationId);
                await emailSvc.SendPaymentConfirmationAsync(jobDb, registrationId, pdfBytes, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to generate receipt for payment {PaymentId}", paymentId);
            }
        });
    }

    private static async Task<RegistrationWorkflowResult<RegistrationCreateOutcome>> RollbackAndFail(
        IDbContextTransaction tx,
        string code,
        string message)
    {
        await tx.RollbackAsync();
        return RegistrationWorkflowResult<RegistrationCreateOutcome>.Fail(code, message);
    }

    private sealed class ExistingParticipantIdentity
    {
        public int ProgramId { get; init; }
        public string FullName { get; init; } = "";
        public DateOnly? DateOfBirth { get; init; }
    }

    private sealed class PendingPaymentItem
    {
        public int GroupId { get; init; }
        public int EventId { get; init; }
        public int ProgramId { get; init; }
        public string ProgramName { get; init; } = "";
        public string Description { get; init; } = "";
        public string? PlayerName { get; init; }
        public decimal Amount { get; init; }
        public int? ParticipantId { get; init; }
    }
}

public sealed class RegistrationValidationOptions
{
    public bool RequireEventOpen { get; init; } = true;
    public bool ValidatePricingAgainstCurrentPrograms { get; init; } = true;
}

public sealed class RegistrationPersistOptions
{
    public bool RequireEventOpen { get; init; } = true;
    public bool ValidatePricingAgainstCurrentPrograms { get; init; } = true;
    public string PaymentGateway { get; init; } = "Stripe";
    public string PaymentMethod { get; init; } = "CreditCard";
    public string PaymentStatus { get; init; } = "P";
    public decimal? PaymentAmountOverride { get; init; }
    public string? GatewaySessionId { get; init; }
    public string? GatewayPaymentId { get; init; }
}

public sealed class PricingQuote
{
    public int EventId { get; init; }
    public string EventName { get; init; } = "";
    public string Currency { get; init; } = "SGD";
    public decimal TotalAmount { get; init; }
    public List<PricingQuoteGroup> Groups { get; init; } = new();
}

public sealed class PricingQuoteGroup
{
    public int ProgramId { get; init; }
    public string ProgramName { get; init; } = "";
    public decimal ExpectedFee { get; init; }
    public bool PaymentRequired { get; init; }
    public string FeeStructure { get; init; } = "per_entry";
    public int ParticipantCount { get; init; }
}

public sealed class RegistrationCreateOutcome
{
    public int RegistrationId { get; init; }
    public int PaymentId { get; init; }
    public decimal TotalAmount { get; init; }
    public string PaymentStatus { get; init; } = "P";
}

public sealed class RegistrationWorkflowResult<T>
{
    public bool Success { get; private init; }
    public string? Code { get; private init; }
    public string Message { get; private init; } = "";
    public T? Value { get; private init; }

    public static RegistrationWorkflowResult<T> Ok(T? value) => new()
    {
        Success = true,
        Value = value,
    };

    public static RegistrationWorkflowResult<T> Fail(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}
