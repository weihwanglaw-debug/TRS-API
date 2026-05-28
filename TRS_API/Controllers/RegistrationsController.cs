using TRS_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stripe;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/registrations")]
public class RegistrationsController : ControllerBase
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pending",
        "Confirmed",
        "Cancelled",
        "CancelPending",
        "RefundFailed",
    };

    private readonly TRSDbContext _db;
    private readonly ILogger<RegistrationsController> _log;
    private readonly IBackgroundJobQueue _jobQueue;
    private readonly ReceiptService _receipt;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly RegistrationWorkflowService _registrationWorkflow;
    public RegistrationsController(
        TRSDbContext db,
        ILogger<RegistrationsController> log,
        ReceiptService receipt,
        IBackgroundJobQueue jobQueue,
        IServiceScopeFactory serviceScopeFactory,
        RegistrationWorkflowService registrationWorkflow)
        => (_db, _log, _receipt, _jobQueue, _serviceScopeFactory, _registrationWorkflow) =
            (db, log, receipt, jobQueue, serviceScopeFactory, registrationWorkflow);

    // -- GET /api/registrations  -- admin, paged + filtered -----------------
    [HttpGet, Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? eventId, [FromQuery] int? programId,
        [FromQuery] string? regStatus, [FromQuery] string? payStatus,
        [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var q = _db.EventRegistrations
            .Include(r => r.ParticipantGroups).ThenInclude(g => g.Participants).ThenInclude(p => p.CustomFieldValues)
            .Include(r => r.Payments).ThenInclude(p => p.Items)
            .AsQueryable();

        if (eventId.HasValue) q = q.Where(r => r.EventId == eventId);
        if (programId.HasValue) q = q.Where(r => r.ParticipantGroups.Any(g => g.ProgramId == programId));
        if (!string.IsNullOrEmpty(regStatus)) q = q.Where(r => r.RegStatus == regStatus);
        if (!string.IsNullOrEmpty(payStatus))
            // Translate long-form frontend code ("Success") â†’ DB short code ("S") before filtering
            q = q.Where(r => r.Payments.Any(p => p.PaymentStatus == PayStatusToDb(payStatus)));
        if (!string.IsNullOrEmpty(search))
            q = q.Where(r => r.ContactName.Contains(search) || r.ContactEmail.Contains(search)
                || r.Payments.Any(p => p.ReceiptNumber!.Contains(search)));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.SubmittedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new
        {
            items = items.Select(MapReg),
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize)
        });
    }

    // -- GET /api/registrations/:id  -- public (for PaymentResult receipt) --
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var reg = await LoadReg(id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });
        return Ok(MapReg(reg));
    }

    // -- POST /api/registrations  -- public ---------------------------------
    [EnableRateLimiting("payment")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRegistrationRequest req)
    {
        var pricing = await _registrationWorkflow.ValidateAndPriceAsync(req);
        if (!pricing.Success)
            return BadRequest(new { code = pricing.Code, message = pricing.Message });

        var paymentStatus = pricing.Value!.TotalAmount == 0 ? "S" : "P";
        var createResult = await _registrationWorkflow.CreateAsync(req, new RegistrationPersistOptions
        {
            RequireEventOpen = true,
            ValidatePricingAgainstCurrentPrograms = true,
            PaymentGateway = req.Payment.Gateway,
            PaymentMethod = req.Payment.Method,
            PaymentStatus = paymentStatus,
        });

        if (!createResult.Success)
        {
            var isNotFound = string.Equals(createResult.Code, "EVENT_NOT_FOUND", StringComparison.Ordinal)
                || string.Equals(createResult.Code, "PROGRAM_NOT_FOUND", StringComparison.Ordinal);
            return isNotFound
                ? NotFound(new { code = createResult.Code, message = createResult.Message })
                : BadRequest(new { code = createResult.Code, message = createResult.Message });
        }

        var createdReg = await LoadReg(createResult.Value!.RegistrationId);
        return Ok(MapReg(createdReg!));
    }

    // -- PATCH /api/registrations/:id/status  -- admin ----------------------
    [HttpPatch("{id:int}/status"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateRegStatusRequest req)
    {
        if (!AllowedStatuses.Contains(req.Status))
            return BadRequest(new { code = "INVALID_STATUS", message = "Status must be Pending, Confirmed, CancelPending, RefundFailed, or Cancelled." });

        var reg = await _db.EventRegistrations.FindAsync(id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });
        reg.RegStatus = req.Status;
        reg.RegistrationStatus = req.Status switch { "Confirmed" => "C", "Cancelled" => "X", _ => "P" };
        if (req.Status == "Confirmed") reg.ConfirmedAt = DateTime.UtcNow;
        reg.UpdatedAt = DateTime.UtcNow;

        // Cascade the same status to every participant group so that capacity
        // counts (which exclude GroupStatus = "Cancelled") and the fixture
        // participant list stay in sync with the registration-level status.
        var groups = await _db.ParticipantGroups
            .Where(g => g.RegistrationId == id)
            .ToListAsync();
        foreach (var g in groups)
        {
            g.GroupStatus = req.Status;
            g.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- PATCH /api/registrations/:id/groups/:gid/status  -- admin ----------
    [HttpPatch("{id:int}/groups/{gid:int}/status"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateGroupStatus(int id, int gid, [FromBody] UpdateRegStatusRequest req)
    {
        if (!AllowedStatuses.Contains(req.Status))
            return BadRequest(new { code = "INVALID_STATUS", message = "Status must be Pending, Confirmed, CancelPending, RefundFailed, or Cancelled." });

        var group = await _db.ParticipantGroups
            .FirstOrDefaultAsync(g => g.GroupId == gid && g.RegistrationId == id);
        if (group == null) return NotFound(new { code = "NOT_FOUND", message = "Group not found." });
        group.GroupStatus = req.Status; group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- PATCH /api/registrations/:id/groups/:gid/seed  -- admin -------------
    [HttpPatch("{id:int}/groups/{gid:int}/seed"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateGroupSeed(int id, int gid, [FromBody] UpdateSeedRequest req)
    {
        var group = await _db.ParticipantGroups
            .FirstOrDefaultAsync(g => g.GroupId == gid && g.RegistrationId == id);
        if (group == null) return NotFound(new { code = "NOT_FOUND", message = "Group not found." });
        group.Seed = req.Seed; group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- GET /api/registrations/:id/payment  -- admin -----------------------
    [HttpGet("{id:int}/payment"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> GetPayment(int id)
    {
        var payment = await _db.Payments
            .Include(p => p.Items)
            .Include(p => p.Refunds)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });
        return Ok(MapPayment(payment));
    }

    // -- PATCH /api/registrations/:id/payment  -- admin (manual confirm) ----
    [HttpPatch("{id:int}/payment"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdatePayment(int id, [FromBody] UpdatePaymentManualRequest req)
    {
        var payment = await _db.Payments
            .Include(p => p.Items)
            .Include(p => p.Refunds)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });

        if (req.Method != null) payment.PaymentMethod = req.Method;

        // Translate long-form frontend status ("Success") â†’ DB short code ("S")
        // This also prevents truncation errors on the VARCHAR(2) column.
        if (req.PaymentStatus != null)
        {
            var targetStatus = PayStatusToDb(req.PaymentStatus);
            if (!CanAdminSetPaymentStatus(payment.PaymentStatus, targetStatus))
                return Conflict(new
                {
                    code = "INVALID_TRANSITION",
                    message = $"Cannot change payment status from {payment.PaymentStatus} to {targetStatus}."
                });
            payment.PaymentStatus = targetStatus;
        }

        if (req.ReceiptNo != null) payment.ReceiptNumber = req.ReceiptNo;

        // payment.PaymentStatus is now always a short code - safe to compare with "S"
        if (payment.PaymentStatus == "S")
        {
            payment.PaidAt = DateTime.UtcNow;
            if (string.IsNullOrEmpty(payment.ReceiptNumber))
            {
                var d = DateTime.UtcNow;
                payment.ReceiptNumber = $"TRS-{d:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}";
            }
            foreach (var item in payment.Items) item.ItemStatus = "S";

            // also flip registration
            var reg = await _db.EventRegistrations.FindAsync(id);
            if (reg != null) { reg.RegStatus = "Confirmed"; reg.RegistrationStatus = "C"; reg.ConfirmedAt = DateTime.UtcNow; }
        }
        payment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Audit log
        _db.PaymentAuditLogs.Add(new PaymentAuditLog
        {
            EntityType = "Payment",
            EntityId = payment.PaymentId,
            Action = "ManualPaymentConfirmed",
            NewStatus = payment.PaymentStatus,   // store short code in audit log
            Reason = req.AdminNote,
            PerformedBy = User.Identity?.Name ?? "admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- GET /api/registrations/:id/payment/refunds  -- admin ---------------
    [HttpGet("{id:int}/payment/refunds"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> GetRefunds(int id)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });
        var refunds = await _db.Refunds
            .Where(r => r.PaymentId == payment.PaymentId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        return Ok(refunds.Select(r => new {
            id = r.RefundId.ToString(),
            paymentId = r.PaymentId.ToString(),
            paymentItemId = r.PaymentItemId.ToString(),
            gateway = r.PaymentGateway,
            gatewayRefundId = r.GatewayRefundId,
            r.RefundAmount,
            r.RefundReason,
            refundStatus = r.RefundStatus,
            requestedBy = r.RequestedBy,
            approvedBy = r.ApprovedBy,
            createdAt = r.CreatedAt,
            processedAt = r.ProcessedAt,
        }));
    }

    // -- POST /api/registrations/:id/payment/refunds  -- admin --------------
    [HttpPost("{id:int}/payment/refunds"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> InitiateRefund(int id, [FromBody] InitiateRefundRequest req)
    {
        var payment = await _db.Payments.Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });

        var item = payment.Items.FirstOrDefault(i => i.PaymentItemId == req.PaymentItemId);
        if (item == null) return NotFound(new { code = "NOT_FOUND", message = "Payment item not found." });
        var result = await ProcessRefundItemAsync(id, payment, item, req.RefundAmount, req.RefundReason);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        var refund = result.Refund!;
        return Ok(new
        {
            id = refund.RefundId.ToString(),
            refundStatus = refund.RefundStatus,
            refundAmount = refund.RefundAmount,
            gatewayRefundId = refund.GatewayRefundId
        });
    }

    [HttpPost("{id:int}/cancel-with-refunds"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> CancelWithRefunds(int id, [FromBody] CancelRegistrationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { code = "REASON_REQUIRED", message = "Cancellation reason is required." });

        var reg = await _db.EventRegistrations
            .Include(r => r.ParticipantGroups)
            .FirstOrDefaultAsync(r => r.RegistrationId == id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });

        var payment = await _db.Payments.Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);

        if (payment == null || payment.Items.All(i => i.ItemStatus != "S"))
        {
            ApplyRegistrationStatus(reg, "Cancelled");
            await _db.SaveChangesAsync();
            var cancelled = await LoadReg(id);
            return Ok(new { registration = MapReg(cancelled!), errors = Array.Empty<string>() });
        }

        ApplyRegistrationStatus(reg, "CancelPending");
        await _db.SaveChangesAsync();

        var errors = new List<string>();
        foreach (var item in payment.Items.Where(i => i.ItemStatus == "S").ToList())
        {
            var alreadyRefunded = await _db.Refunds
                .Where(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "S")
                .SumAsync(r => (decimal?)r.RefundAmount) ?? 0m;
            var remainingRefundAmount = item.Amount - alreadyRefunded;
            if (remainingRefundAmount <= 0)
                continue;

            var refund = await ProcessRefundItemAsync(
                id,
                payment,
                item,
                remainingRefundAmount,
                $"Cancelled: {req.Reason}");

            if (!refund.Success)
                errors.Add($"{item.ProgramName}: {refund.Message}");
        }

        var hasRemainingRefundableItems = payment.Items.Any(i => i.ItemStatus == "S");
        ApplyRegistrationStatus(reg, errors.Count == 0 && !hasRemainingRefundableItems ? "Cancelled" : "RefundFailed");
        await _db.SaveChangesAsync();

        var updated = await LoadReg(id);
        return Ok(new { registration = MapReg(updated!), errors });
    }

    // -- GET /api/registrations/export  -- admin -----------------------------
    [HttpGet("export"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> Export([FromQuery] int? eventId, [FromQuery] int? programId)
    {
        var q = _db.EventRegistrations
            .Include(r => r.ParticipantGroups).ThenInclude(g => g.Participants).ThenInclude(p => p.CustomFieldValues)
            .Include(r => r.Payments).ThenInclude(p => p.Items)
            .AsQueryable();
        if (eventId.HasValue) q = q.Where(r => r.EventId == eventId);
        if (programId.HasValue) q = q.Where(r => r.ParticipantGroups.Any(g => g.ProgramId == programId));
        var items = await q.OrderByDescending(r => r.SubmittedAt).ToListAsync();
        return Ok(items.Select(MapReg));
    }

    // -- GET /api/registrations/stats  -- admin ------------------------------
    [HttpGet("stats"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> Stats([FromQuery] int? eventId)
    {
        var q = _db.EventRegistrations.Include(r => r.Payments).AsQueryable();
        if (eventId.HasValue) q = q.Where(r => r.EventId == eventId);
        var all = await q.ToListAsync();
        return Ok(new
        {
            totalRegistrations = all.Count,
            confirmed = all.Count(r => r.RegStatus == "Confirmed"),
            pending = all.Count(r => r.RegStatus == "Pending"),
            cancelled = all.Count(r => r.RegStatus == "Cancelled"),
            totalRevenue = all.Where(r => r.Payments.Any(p => p.PaymentStatus == "S"))
                             .Sum(r => r.Payments.Where(p => p.PaymentStatus == "S").Sum(p => p.Amount)),
            pendingPayments = all.Count(r => r.Payments.Any(p => p.PaymentStatus == "P")),
        });
    }

    // -- GET /api/registrations/:id/receipt  -- public -------------------------
    [HttpGet("{id:int}/receipt")]
    public async Task<IActionResult> GetReceipt(int id)
    {
        try
        {
            var bytes = await _receipt.GenerateAsync(_db, id);
            var reg = await _db.EventRegistrations
                .Include(r => r.Payments)
                .FirstOrDefaultAsync(r => r.RegistrationId == id);
            var receiptNo = reg?.Payments.FirstOrDefault()?.ReceiptNumber ?? $"TRS-{id:D6}";
            return File(bytes, "application/pdf", $"Receipt-{receiptNo}.pdf");
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error generating receipt for registration {Id}", id);
            return StatusCode(500, new { code = "RECEIPT_ERROR", message = "Failed to generate receipt." });
        }
    }


    // -- PATCH /api/registrations/:id/participants/:pid  -- admin -----------
    // Update individual participant details.
    // TODO (future): write each changed field to ParticipantAuditLog with
    //   OldValue, NewValue, ModifiedBy, ModifiedAt for full change history.
    [HttpPatch("{id:int}/participants/{pid:int}"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateParticipant(int id, int pid, [FromBody] UpdateParticipantRequest req)
    {
        // Verify the participant belongs to this registration
        var participant = await _db.Participants
            .Include(p => p.Group)
            .Include(p => p.CustomFieldValues)
            .FirstOrDefaultAsync(p => p.ParticipantId == pid && p.Group.RegistrationId == id);

        if (participant == null)
            return NotFound(new { code = "NOT_FOUND", message = "Participant not found in this registration." });

        // ── Duplicate check — only when FullName or Dob is being changed ──────
        // Excludes self (pid) so editing other fields on an existing participant
        // does not false-flag against its own record.
        if (req.FullName != null || req.Dob != null)
        {
            var checkName = req.FullName ?? participant.FullName;
            var checkDob  = participant.DateOfBirth; // default to existing
            if (req.Dob != null && DateOnly.TryParse(req.Dob, out var parsedDob))
                checkDob = parsedDob;

            var isDuplicate = await _db.Participants
                .Where(p =>
                    p.ParticipantId != pid &&
                    p.Group.ProgramId == participant.Group.ProgramId &&
                    p.Group.GroupStatus != "Cancelled" &&
                    p.FullName == checkName &&
                    p.DateOfBirth == checkDob)
                .AnyAsync();

            if (isDuplicate)
                return Conflict(new {
                    code    = "DUPLICATE_PARTICIPANT",
                    message = $"{checkName} with this date of birth is already registered in this program."
                });
        }

        // Capture old values for audit (TODO: write to ParticipantAuditLog)
        // var oldValues = new { participant.FullName, participant.DateOfBirth, ... };

        if (req.FullName          != null) participant.FullName          = req.FullName;
        if (req.Gender            != null) participant.Gender            = req.Gender;
        if (req.Nationality       != null) participant.Nationality       = req.Nationality;
        if (req.ClubSchoolCompany != null) participant.ClubSchoolCompany = req.ClubSchoolCompany;
        if (req.Email             != null) participant.Email             = req.Email;
        if (req.ContactNumber     != null) participant.ContactNumber     = req.ContactNumber;
        if (req.TshirtSize        != null) participant.TshirtSize        = req.TshirtSize;
        if (req.SbaId             != null) participant.SbaId             = req.SbaId;
        if (req.GuardianName      != null) participant.GuardianName      = req.GuardianName;
        if (req.GuardianContact   != null) participant.GuardianContact   = req.GuardianContact;
        if (req.Remark            != null) participant.Remark            = req.Remark;
        if (req.DocumentUrl       != null) participant.DocumentUrl       = req.DocumentUrl;

        if (req.Dob != null)
            participant.DateOfBirth = string.IsNullOrWhiteSpace(req.Dob)
                ? null
                : DateOnly.Parse(req.Dob);

        // Update custom field values (upsert by label)
        if (req.CustomFieldValues != null)
        {
            // Load program custom field definitions to resolve CustomFieldId
            // for labels that have no existing row (field added after registration).
            var programFields = await _db.ProgramCustomFields
                .Where(cf => cf.ProgramId == participant.Group.ProgramId)
                .ToDictionaryAsync(cf => cf.Label, cf => cf.CustomFieldId);

            foreach (var (label, value) in req.CustomFieldValues)
            {
                var existing = participant.CustomFieldValues
                    .FirstOrDefault(cf => cf.FieldLabel == label);

                if (existing != null)
                {
                    existing.FieldValue = value;
                }
                else if (programFields.TryGetValue(label, out var customFieldId))
                {
                    // Insert new row — field was added to the program after this
                    // participant registered so no row existed yet.
                    _db.ParticipantCustomFieldValues.Add(new ParticipantCustomFieldValue
                    {
                        ParticipantId = participant.ParticipantId,
                        CustomFieldId = customFieldId,
                        FieldLabel    = label,
                        FieldValue    = value,
                    });
                }
                // Label not found in program fields = renamed/deleted — skip silently.
            }
        }

        participant.UpdatedAt = DateTime.UtcNow;

        // TODO: write ParticipantAuditLog entries here (one per changed field)
        // _db.ParticipantAuditLogs.Add(new ParticipantAuditLog { ... });

        await _db.SaveChangesAsync();

        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- POST /api/registrations/:id/confirm  -- admin ---------------------
    // Admin confirms a registration directly - bypasses online payment.
    // Supports three payment outcomes:
    //   S  = Paid (manual confirmation - cash/bank/PayNow collected)
    //   W  = Waived (admin waives the fee entirely - VIP, staff, error correction)
    //   PC = Pending Collection (registration confirmed, payment to be collected later)
    [HttpPost("{id:int}/confirm"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> ConfirmRegistration(int id, [FromBody] ConfirmRegistrationRequest req)
    {
        var allowedStatuses = new[] { "S", "W", "PC" };
        var status = PayStatusToDb(req.PaymentStatus);
        if (!allowedStatuses.Contains(status))
            return BadRequest(new { code = "INVALID_STATUS", message = "PaymentStatus must be S (Paid), W (Waived), or PC (Pending Collection)." });

        var reg = await LoadReg(id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });

        var payment = reg.Payments.FirstOrDefault();

        if (payment == null)
        {
            // Create a manual payment record for free/waived registrations
            payment = new Payment
            {
                RegistrationId = id,
                EventId        = reg.EventId,
                PaymentGateway = "Manual",
                PaymentMethod  = req.Method ?? (status == "W" ? "Others" : "Cash"),
                Amount         = reg.ParticipantGroups.Sum(g => g.Fee),
                Currency       = "SGD",
                PaymentStatus  = status,
                AdminNote      = req.AdminNote,
                CreatedAt      = DateTime.UtcNow,
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();
        }
        else
        {
            if (!CanAdminSetPaymentStatus(payment.PaymentStatus, status))
                return Conflict(new
                {
                    code = "INVALID_TRANSITION",
                    message = $"Cannot change payment status from {payment.PaymentStatus} to {status}."
                });
            if (req.Method != null) payment.PaymentMethod = req.Method;
            payment.PaymentStatus = status;
            payment.AdminNote     = req.AdminNote;
            if (req.PaymentReference != null) payment.ReceiptNumber = req.PaymentReference;
            payment.UpdatedAt = DateTime.UtcNow;
        }

        // For Paid or Waived: stamp paidAt, generate receipt, flip items to S
        if (status == "S" || status == "W")
        {
            payment.PaidAt = DateTime.UtcNow;
            if (string.IsNullOrEmpty(payment.ReceiptNumber))
            {
                var d = DateTime.UtcNow;
                payment.ReceiptNumber = $"TRS-{d:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}";
            }
            foreach (var item in payment.Items) { item.ItemStatus = "S"; item.UpdatedAt = DateTime.UtcNow; }
        }

        // Confirm the registration regardless of payment status
        reg.RegStatus         = "Confirmed";
        reg.RegistrationStatus = "C";
        reg.ConfirmedAt       = DateTime.UtcNow;
        reg.UpdatedAt         = DateTime.UtcNow;

        // Cascade status to all participant groups
        foreach (var g in reg.ParticipantGroups)
        {
            g.GroupStatus = "Confirmed";
            g.UpdatedAt   = DateTime.UtcNow;
        }

        _db.PaymentAuditLogs.Add(new PaymentAuditLog
        {
            EntityType  = "Payment",
            EntityId    = payment.PaymentId,
            Action      = $"AdminConfirm_{status}",
            NewStatus   = status,
            Reason      = req.AdminNote,
            PerformedBy = User.Identity?.Name ?? "admin",
            IpAddress   = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt   = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();

        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // -- Load helper ----------------------------------------------------------
    private Task<EventRegistration?> LoadReg(int id) =>
        _db.EventRegistrations
            .Include(r => r.ParticipantGroups).ThenInclude(g => g.Participants)
            .ThenInclude(p => p.CustomFieldValues)
            .Include(r => r.Payments).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(r => r.RegistrationId == id);

    // -- Status code translation helpers --------------------------------------
    // DB stores short codes; the frontend TypeScript types use long names.
    // All translation is centralised here so no other file needs to change.

    private async Task<RefundOperationResult> ProcessRefundItemAsync(
        int registrationId,
        Payment payment,
        PaymentItem item,
        decimal refundAmount,
        string? refundReason)
    {
        var successfulRefunds = await _db.Refunds
            .Where(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "S")
            .ToListAsync();
        var refundedAmount = successfulRefunds.Sum(r => r.RefundAmount);
        var remainingAmount = item.Amount - refundedAmount;
        if (remainingAmount <= 0)
        {
            await ReconcileRefundedAmountsAsync(payment, item);
            return RefundOperationResult.Fail("ALREADY_REFUNDED", "This item is already fully refunded.");
        }

        if (item.ItemStatus != "S")
            return RefundOperationResult.Fail("INVALID_STATE", "Only confirmed items can be refunded.");
        if (refundAmount > remainingAmount)
            return RefundOperationResult.Fail("OVER_REFUND", $"Maximum refundable is {remainingAmount}.");

        var refund = await _db.Refunds
            .FirstOrDefaultAsync(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "P");
        if (refund != null)
        {
            if (!string.IsNullOrEmpty(refund.GatewayRefundId))
                return RefundOperationResult.Fail("REFUND_IN_PROGRESS", "A refund for this item is already in progress.");
            if (refund.RefundAmount != refundAmount)
                return RefundOperationResult.Fail("REFUND_IN_PROGRESS", "A pending refund exists for this item with a different amount.");
        }
        else
        {
            refund = new TRS_Data.Models.Refund
            {
                PaymentId = payment.PaymentId,
                PaymentItemId = item.PaymentItemId,
                PaymentGateway = payment.PaymentGateway,
                RefundAmount = refundAmount,
                RefundReason = refundReason,
                RefundStatus = "P",
                RequestedBy = User.Identity?.Name ?? "admin",
                CreatedAt = DateTime.UtcNow,
            };
            _db.Refunds.Add(refund);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _log.LogWarning(ex,
                    "Duplicate active refund prevented for payment item {PaymentItemId}",
                    item.PaymentItemId);
                return RefundOperationResult.Fail("REFUND_IN_PROGRESS", "A refund for this item is already in progress.");
            }
        }

        try
        {
            if (payment.PaymentGateway == "Stripe")
            {
                var stripeRefund = await new RefundService().CreateAsync(
                    new RefundCreateOptions
                    {
                        PaymentIntent = payment.GatewayPaymentId,
                        Amount = (long)(refundAmount * 100),
                        Reason = "requested_by_customer",
                        Metadata = new Dictionary<string, string>
                        {
                            ["registration_id"] = registrationId.ToString(),
                            ["payment_item_id"] = item.PaymentItemId.ToString(),
                        }
                    },
                    new RequestOptions { IdempotencyKey = $"trs_refund_{refund.RefundId}" });

                refund.GatewayRefundId = stripeRefund.Id;
                refund.RefundStatus = stripeRefund.Status == "failed" ? "F" : "S";
                refund.ProcessedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            else
            {
                refund.RefundStatus = "S";
                refund.ProcessedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
        catch (StripeException ex)
        {
            refund.RefundStatus = "F";
            refund.ProcessedAt = DateTime.UtcNow;
            _db.PaymentAuditLogs.Add(new PaymentAuditLog
            {
                EntityType = "Refund",
                EntityId = refund.RefundId,
                Action = "RefundFailed",
                Reason = refundReason,
                PerformedBy = User.Identity?.Name ?? "admin",
                Notes = ex.StripeError?.Message ?? ex.Message,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
            return RefundOperationResult.Fail(
                ex.StripeError?.Code ?? "REFUND_FAILED",
                ex.StripeError?.Message ?? "Refund failed.");
        }

        if (refund.RefundStatus != "S")
            return RefundOperationResult.Fail("REFUND_FAILED", "Refund did not complete successfully.");

        await ReconcileSuccessfulRefundAsync(payment, item, refund, refundReason);
        return RefundOperationResult.Ok(refund);
    }

    private async Task ReconcileSuccessfulRefundAsync(
        Payment payment,
        PaymentItem item,
        TRS_Data.Models.Refund refund,
        string? refundReason)
    {
        await ReconcileRefundedAmountsAsync(payment, item);
        _db.PaymentAuditLogs.Add(new PaymentAuditLog
        {
            EntityType = "Refund",
            EntityId = refund.RefundId,
            Action = "RefundInitiated",
            Reason = refundReason,
            PerformedBy = User.Identity?.Name ?? "admin",
            Notes = $"PaymentItemId={item.PaymentItemId}, Amount={refund.RefundAmount}",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task ReconcileRefundedAmountsAsync(Payment payment, PaymentItem item)
    {
        var itemRefundedAmount = await _db.Refunds
            .Where(r => r.PaymentItemId == item.PaymentItemId && r.RefundStatus == "S")
            .SumAsync(r => (decimal?)r.RefundAmount) ?? 0m;

        var newItemStatus = itemRefundedAmount >= item.Amount ? "R" : "S";
        if (item.ItemStatus != newItemStatus)
        {
            item.ItemStatus = newItemStatus;
            item.UpdatedAt = DateTime.UtcNow;
        }

        var paymentRefundedAmount = await _db.Refunds
            .Where(r => r.PaymentId == payment.PaymentId && r.RefundStatus == "S")
            .SumAsync(r => (decimal?)r.RefundAmount) ?? 0m;

        payment.PaymentStatus = paymentRefundedAmount switch
        {
            <= 0m => "S",
            var amount when amount >= payment.Amount => "FR",
            _ => "PR",
        };
        payment.UpdatedAt = DateTime.UtcNow;
    }

    private static void ApplyRegistrationStatus(EventRegistration reg, string status)
    {
        reg.RegStatus = status;
        reg.RegistrationStatus = status switch { "Confirmed" => "C", "Cancelled" => "X", _ => "P" };
        if (status == "Confirmed") reg.ConfirmedAt = DateTime.UtcNow;
        reg.UpdatedAt = DateTime.UtcNow;
        foreach (var group in reg.ParticipantGroups)
        {
            group.GroupStatus = status;
            group.UpdatedAt = DateTime.UtcNow;
        }
    }

    private sealed class RefundOperationResult
    {
        public bool Success { get; private init; }
        public string? Code { get; private init; }
        public string? Message { get; private init; }
        public TRS_Data.Models.Refund? Refund { get; private init; }

        public static RefundOperationResult Ok(TRS_Data.Models.Refund refund) => new()
        {
            Success = true,
            Refund = refund,
        };

        public static RefundOperationResult Fail(string code, string message) => new()
        {
            Success = false,
            Code = code,
            Message = message,
        };
    }

    private static string PayStatusToDb(string s) => s switch
    {
        "Success"            => "S",
        "Pending"            => "P",
        "PartiallyRefunded"  => "PR",
        "FullyRefunded"      => "FR",
        "Failed"             => "F",
        "Cancelled"          => "X",
        "Waived"             => "W",
        "PendingCollection"  => "PC",
        _                    => s    // already a short code - pass through
    };

    private static bool CanAdminSetPaymentStatus(string currentStatus, string targetStatus)
    {
        if (currentStatus == targetStatus) return true;
        if (currentStatus is "PR" or "FR" or "F" or "X") return false;
        if (currentStatus == "S") return targetStatus == "S";
        if (currentStatus == "W") return targetStatus == "W";
        return (currentStatus is "P" or "PC") && (targetStatus is "S" or "W" or "PC");
    }

    // -- Map helpers ----------------------------------------------------------
    private static object MapPayment(Payment p) => new
    {
        id = p.PaymentId.ToString(),
        registrationId = p.RegistrationId.ToString(),
        eventId = p.EventId.ToString(),
        gateway = p.PaymentGateway,
        method = p.PaymentMethod,
        amount = p.Amount,
        currency = p.Currency,
        paymentStatus = p.PaymentStatus,
        receiptNo = p.ReceiptNumber,
        gatewaySessionId = p.GatewaySessionId,
        gatewayPaymentId = p.GatewayPaymentId,
        gatewayChargeId = p.GatewayChargeId,
        createdAt = p.CreatedAt,
        paidAt = p.PaidAt,
        adminNote = p.AdminNote,
        items = p.Items.Select(i => new {
            id = i.PaymentItemId.ToString(),
            paymentId = i.PaymentId.ToString(),
            participantGroupId = i.GroupId.ToString(),
            participantId = i.ParticipantId?.ToString(),
            i.ProgramName,
            i.Description,
            i.PlayerName,
            i.Amount,
            itemStatus = i.ItemStatus,
        }).ToList()
    };

    private static object MapReg(EventRegistration r)
    {
        var payment = r.Payments.FirstOrDefault();
        return new
        {
            id = r.RegistrationId.ToString(),
            eventId = r.EventId.ToString(),
            eventName = r.EventName,
            submittedAt = r.SubmittedAt,
            regStatus = r.RegStatus,
            contactName = r.ContactName,
            contactEmail = r.ContactEmail,
            contactPhone = r.ContactPhone,
            groups = r.ParticipantGroups.Select(g => new {
                id = g.GroupId.ToString(),
                registrationId = r.RegistrationId.ToString(),
                eventId = g.EventId.ToString(),
                programId = g.ProgramId.ToString(),
                g.ProgramName,
                g.Fee,
                groupStatus = g.GroupStatus,
                g.Seed,
                clubDisplay = g.ClubDisplay ?? "",
                namesDisplay = g.NamesDisplay ?? "",
                participants = g.Participants.Select(p => new {
                    id = p.ParticipantId.ToString(),
                    participantGroupId = g.GroupId.ToString(),
                    p.FullName,
                    dob = p.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
                    p.Gender,
                    p.Nationality,
                    p.ClubSchoolCompany,
                    p.Email,
                    p.ContactNumber,
                    p.TshirtSize,
                    p.SbaId,
                    p.GuardianName,
                    p.GuardianContact,
                    p.DocumentUrl,
                    p.Remark,
                    // Return label-keyed dict to match what the frontend sent on create
                    customFieldValues = p.CustomFieldValues
                        .ToDictionary(
                            cf => cf.FieldLabel ?? cf.CustomFieldId.ToString(),
                            cf => cf.FieldValue ?? ""),
                }).ToList()
            }).ToList(),
            payment = payment == null ? null : MapPayment(payment)
        };
    }
}
