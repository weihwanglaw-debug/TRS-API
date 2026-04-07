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
    };

    private readonly TRSDbContext _db;
    private readonly ILogger<RegistrationsController> _log;
    private readonly IBackgroundJobQueue _jobQueue;
    private readonly ReceiptService _receipt;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    public RegistrationsController(
        TRSDbContext db,
        ILogger<RegistrationsController> log,
        ReceiptService receipt,
        IBackgroundJobQueue jobQueue,
        IServiceScopeFactory serviceScopeFactory)
        => (_db, _log, _receipt, _jobQueue, _serviceScopeFactory) = (db, log, receipt, jobQueue, serviceScopeFactory);

    // ── GET /api/registrations  ── admin, paged + filtered ─────────────────
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
            // Translate long-form frontend code ("Success") → DB short code ("S") before filtering
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

    // ── GET /api/registrations/:id  ── public (for PaymentResult receipt) ──
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var reg = await LoadReg(id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });
        return Ok(MapReg(reg));
    }

    // ── POST /api/registrations  ── public ─────────────────────────────────
    [EnableRateLimiting("payment")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRegistrationRequest req)
    {
        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Pre-load custom fields for all programs in this registration so we can
            // resolve label → CustomFieldId without N+1 queries inside the loop.
            var programIds = req.Groups.Select(g => g.ProgramId).Distinct().ToList();
            var customFieldsByProgram = await _db.ProgramCustomFields
                .Where(cf => programIds.Contains(cf.ProgramId))
                .GroupBy(cf => cf.ProgramId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.ToDictionary(cf => cf.Label, cf => cf.CustomFieldId));

            var reg = new EventRegistration
            {
                EventId = req.EventId,
                EventName = req.EventName,
                RegStatus = "Pending",
                ContactName = req.ContactName,
                ContactEmail = req.ContactEmail,
                ContactPhone = req.ContactPhone,
                SubmittedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                // legacy fields
                TotalAmount = req.Payment.Amount,
                Currency = req.Payment.Currency,
                RegistrationStatus = "P",
            };
            _db.EventRegistrations.Add(reg);
            await _db.SaveChangesAsync();   // get RegistrationId

            var groups = new List<ParticipantGroup>();
            var allItems = new List<PaymentItem>();

            for (int gi = 0; gi < req.Groups.Count; gi++)
            {
                var gDto = req.Groups[gi];

                // ── Capacity check (race-condition safe) ──────────────────────
                var program = await _db.Programs
                    .FromSqlRaw(
                        "SELECT * FROM Programs WITH (UPDLOCK, ROWLOCK) WHERE ProgramID = {0}",
                        gDto.ProgramId)
                    .FirstOrDefaultAsync();

                if (program == null)
                {
                    await tx.RollbackAsync();
                    return NotFound(new
                    {
                        code = "PROGRAM_NOT_FOUND",
                        message = $"Program '{gDto.ProgramName}' not found."
                    });
                }

                if (!program.IsActive || program.Status == "closed")
                {
                    await tx.RollbackAsync();
                    return BadRequest(new
                    {
                        code = "PROGRAM_CLOSED",
                        message = $"'{gDto.ProgramName}' is no longer accepting registrations."
                    });
                }

                var activeGroupCount = await _db.ParticipantGroups
                    .CountAsync(g => g.ProgramId == gDto.ProgramId
                        && g.GroupStatus != "Cancelled");

                if (activeGroupCount >= program.MaxParticipants)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new
                    {
                        code = "PROGRAM_FULL",
                        message = $"'{gDto.ProgramName}' is full. No slots remaining."
                    });
                }

                // ── Duplicate check (participant identity: name + DOB) ────────
                // Matches on the actual participants being registered, not the
                // contact email — this allows a parent to register two different
                // children in the same program under their own email address.
                var incomingParticipants = gDto.Participants
                    .Select(p => new
                    {
                        p.FullName,
                        Dob = string.IsNullOrWhiteSpace(p.Dob) ? (DateOnly?)null : DateOnly.Parse(p.Dob),
                    })
                    .ToList();

                var isDuplicate = await _db.ParticipantGroups
                    .AnyAsync(g => g.ProgramId == gDto.ProgramId
                        && g.GroupStatus != "Cancelled"
                        && g.Participants.Any(existing => incomingParticipants.Any(incoming =>
                            incoming.FullName == existing.FullName
                            && incoming.Dob == existing.DateOfBirth)));

                if (isDuplicate)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new
                    {
                        code = "DUPLICATE_REGISTRATION",
                        message = $"One or more participants are already registered for '{gDto.ProgramName}'."
                    });
                }

                var group = new ParticipantGroup
                {
                    RegistrationId = reg.RegistrationId,
                    EventId = req.EventId,
                    ProgramId = gDto.ProgramId,
                    ProgramName = gDto.ProgramName,
                    Fee = gDto.Fee,
                    GroupStatus = "Pending",
                    CreatedAt = DateTime.UtcNow,
                };
                _db.ParticipantGroups.Add(group);
                await _db.SaveChangesAsync();   // get GroupId

                var parts = new List<Participant>();
                foreach (var pDto in gDto.Participants)
                {
                    var p = new Participant
                    {
                        GroupId = group.GroupId,
                        FullName = pDto.FullName,
                        DateOfBirth = pDto.Dob != null ? DateOnly.Parse(pDto.Dob) : null,
                        Gender = pDto.Gender,
                        Nationality = pDto.Nationality,
                        ClubSchoolCompany = pDto.ClubSchoolCompany,
                        Email = pDto.Email,
                        ContactNumber = pDto.ContactNumber,
                        TshirtSize = pDto.TshirtSize,
                        SbaId = pDto.SbaId,
                        GuardianName = pDto.GuardianName,
                        GuardianContact = pDto.GuardianContact,
                        Remark = pDto.Remark,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _db.Participants.Add(p);
                    parts.Add(p);
                }
                await _db.SaveChangesAsync();   // get ParticipantIds

                // ── Custom field values ───────────────────────────────────────
                // Frontend sends { "Field Label": "value" } — resolve label → CustomFieldId
                // using the pre-loaded lookup dict. FK requires a valid CustomFieldId.
                var cfLookup = customFieldsByProgram.GetValueOrDefault(gDto.ProgramId)
                               ?? new Dictionary<string, int>();

                for (int pi = 0; pi < gDto.Participants.Count; pi++)
                {
                    foreach (var (label, val) in gDto.Participants[pi].CustomFieldValues)
                    {
                        if (!cfLookup.TryGetValue(label, out var cfId))
                        {
                            _log.LogWarning(
                                "Custom field label '{Label}' not found for program {ProgramId} — skipping",
                                label, gDto.ProgramId);
                            continue;   // skip unknown labels rather than saving orphaned rows
                        }
                        _db.ParticipantCustomFieldValues.Add(new ParticipantCustomFieldValue
                        {
                            ParticipantId = parts[pi].ParticipantId,
                            CustomFieldId = cfId,
                            FieldLabel    = label,
                            FieldValue    = val,
                        });
                    }
                }

                // display fields
                group.ClubDisplay = parts.FirstOrDefault()?.ClubSchoolCompany ?? "";
                group.NamesDisplay = string.Join(" / ", parts.Select(p => p.FullName));

                // payment items for this group
                foreach (var iDto in gDto.Items)
                {
                    int? participantId = null;
                    if (iDto.ParticipantIndex.HasValue && iDto.ParticipantIndex < parts.Count)
                        participantId = parts[iDto.ParticipantIndex.Value].ParticipantId;

                    allItems.Add(new PaymentItem
                    {
                        GroupId = group.GroupId,
                        EventId = req.EventId,
                        ProgramId = gDto.ProgramId,
                        ProgramName = iDto.ProgramName,
                        Description = iDto.Description,
                        PlayerName = iDto.PlayerName,
                        Amount = iDto.Amount,
                        ItemStatus = "P",
                        CreatedAt = DateTime.UtcNow,
                        ParticipantId = participantId,
                    });
                }
                groups.Add(group);
            }

            var isFreeRegistration = reg.TotalAmount == 0;
            var receiptNo = isFreeRegistration
                ? $"TRS-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999):D5}"
                : null;

            // Payment record
            var payment = new Payment
            {
                RegistrationId = reg.RegistrationId,
                EventId = req.EventId,
                PaymentGateway = req.Payment.Gateway,
                PaymentMethod = req.Payment.Method,
                Amount = req.Payment.Amount,
                Currency = req.Payment.Currency,
                PaymentStatus = isFreeRegistration ? "S" : "P",
                CreatedAt = DateTime.UtcNow,
                PaidAt = isFreeRegistration ? DateTime.UtcNow : null,
                ReceiptNumber = receiptNo,
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync();

            foreach (var item in allItems)
            {
                item.PaymentId = payment.PaymentId;
                if (isFreeRegistration) item.ItemStatus = "S";
                _db.PaymentItems.Add(item);
            }

            if (isFreeRegistration)
            {
                reg.RegStatus = "Confirmed";
                reg.RegistrationStatus = "C";
                reg.ConfirmedAt = DateTime.UtcNow;
                foreach (var group in groups) group.GroupStatus = "Confirmed";
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            if (isFreeRegistration)
            {
                var regIdForJob = reg.RegistrationId;
                var payIdForJob = payment.PaymentId;
                await _jobQueue.EnqueueAsync(async ct =>
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var receiptSvc = scope.ServiceProvider.GetRequiredService<ReceiptService>();
                    var emailSvc = scope.ServiceProvider.GetRequiredService<EmailService>();
                    var jobDb = scope.ServiceProvider.GetRequiredService<TRSDbContext>();
                    try
                    {
                        var pdfBytes = await receiptSvc.GenerateAsync(jobDb, regIdForJob);
                        await emailSvc.SendPaymentConfirmationAsync(jobDb, regIdForJob, pdfBytes, ct);
                        _log.LogInformation("Receipt generated for free registration {RegId}", regIdForJob);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to generate receipt for free registration payment {PaymentId}", payIdForJob);
                    }
                });
            }

            var created = await LoadReg(reg.RegistrationId);
            return Ok(MapReg(created!));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _log.LogError(ex, "Error creating registration");
            return StatusCode(500, new { code = "CREATE_FAILED", message = "Failed to create registration." });
        }
    }

    // ── PATCH /api/registrations/:id/status  ── admin ──────────────────────
    [HttpPatch("{id:int}/status"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateRegStatusRequest req)
    {
        if (!AllowedStatuses.Contains(req.Status))
            return BadRequest(new { code = "INVALID_STATUS", message = "Status must be Pending, Confirmed, or Cancelled." });

        var reg = await _db.EventRegistrations.FindAsync(id);
        if (reg == null) return NotFound(new { code = "NOT_FOUND", message = "Registration not found." });
        reg.RegStatus = req.Status;
        reg.RegistrationStatus = req.Status switch { "Confirmed" => "C", "Cancelled" => "X", _ => "P" };
        if (req.Status == "Confirmed") reg.ConfirmedAt = DateTime.UtcNow;
        reg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // ── PATCH /api/registrations/:id/groups/:gid/status  ── admin ──────────
    [HttpPatch("{id:int}/groups/{gid:int}/status"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateGroupStatus(int id, int gid, [FromBody] UpdateRegStatusRequest req)
    {
        if (!AllowedStatuses.Contains(req.Status))
            return BadRequest(new { code = "INVALID_STATUS", message = "Status must be Pending, Confirmed, or Cancelled." });

        var group = await _db.ParticipantGroups
            .FirstOrDefaultAsync(g => g.GroupId == gid && g.RegistrationId == id);
        if (group == null) return NotFound(new { code = "NOT_FOUND", message = "Group not found." });
        group.GroupStatus = req.Status; group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var updated = await LoadReg(id);
        return Ok(MapReg(updated!));
    }

    // ── PATCH /api/registrations/:id/groups/:gid/seed  ── admin ─────────────
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

    // ── GET /api/registrations/:id/payment  ── admin ───────────────────────
    [HttpGet("{id:int}/payment"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> GetPayment(int id)
    {
        var payment = await _db.Payments.Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });
        return Ok(MapPayment(payment));
    }

    // ── PATCH /api/registrations/:id/payment  ── admin (manual confirm) ────
    [HttpPatch("{id:int}/payment"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdatePayment(int id, [FromBody] UpdatePaymentManualRequest req)
    {
        var payment = await _db.Payments.Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });

        if (req.Method != null) payment.PaymentMethod = req.Method;

        // Translate long-form frontend status ("Success") → DB short code ("S")
        // This also prevents truncation errors on the VARCHAR(2) column.
        if (req.PaymentStatus != null)
            payment.PaymentStatus = PayStatusToDb(req.PaymentStatus);

        if (req.ReceiptNo != null) payment.ReceiptNumber = req.ReceiptNo;

        // payment.PaymentStatus is now always a short code — safe to compare with "S"
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

    // ── GET /api/registrations/:id/payment/refunds  ── admin ───────────────
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

    // ── POST /api/registrations/:id/payment/refunds  ── admin ──────────────
    [HttpPost("{id:int}/payment/refunds"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> InitiateRefund(int id, [FromBody] InitiateRefundRequest req)
    {
        var payment = await _db.Payments.Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.RegistrationId == id);
        if (payment == null) return NotFound(new { code = "NOT_FOUND", message = "Payment not found." });

        var item = payment.Items.FirstOrDefault(i => i.PaymentItemId == req.PaymentItemId);
        if (item == null) return NotFound(new { code = "NOT_FOUND", message = "Payment item not found." });
        if (item.ItemStatus != "S")
            return BadRequest(new { code = "INVALID_STATE", message = "Only confirmed items can be refunded." });
        if (await _db.Refunds.AnyAsync(r => r.PaymentItemId == req.PaymentItemId && r.RefundStatus == "P"))
            return BadRequest(new { code = "REFUND_IN_PROGRESS", message = "A refund for this item is already in progress." });
        if (req.RefundAmount > item.Amount)
            return BadRequest(new { code = "OVER_REFUND", message = $"Maximum refundable is {item.Amount}." });

        var refund = new TRS_Data.Models.Refund
        {
            PaymentId = payment.PaymentId,
            PaymentItemId = req.PaymentItemId,
            PaymentGateway = payment.PaymentGateway,
            RefundAmount = req.RefundAmount,
            RefundReason = req.RefundReason,
            RefundStatus = "P",
            RequestedBy = User.Identity?.Name ?? "admin",
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            if (payment.PaymentGateway == "Stripe")
            {
                var refundOptions = new RefundCreateOptions
                {
                    PaymentIntent = payment.GatewayPaymentId,
                    Amount = (long)(req.RefundAmount * 100),
                    Reason = "requested_by_customer",
                    Metadata = new Dictionary<string, string>
                    {
                        ["registration_id"] = id.ToString(),
                        ["payment_item_id"] = req.PaymentItemId.ToString(),
                    }
                };

                var stripeRefund = await new RefundService().CreateAsync(refundOptions);
                refund.GatewayRefundId = stripeRefund.Id;
                refund.RefundStatus = stripeRefund.Status == "failed" ? "F" : "S";
                refund.ProcessedAt = DateTime.UtcNow;
            }
            else
            {
                refund.RefundStatus = "S";
                refund.ProcessedAt = DateTime.UtcNow;
            }
        }
        catch (StripeException ex)
        {
            return BadRequest(new
            {
                code = ex.StripeError?.Code ?? "REFUND_FAILED",
                message = ex.StripeError?.Message ?? "Refund failed."
            });
        }

        _db.Refunds.Add(refund);

        if (refund.RefundStatus == "S")
        {
            item.ItemStatus = "R";
            item.UpdatedAt = DateTime.UtcNow;
            PaymentController.ApplyRefundOutcome(payment);
        }

        _db.PaymentAuditLogs.Add(new PaymentAuditLog
        {
            EntityType = "Refund",
            EntityId = 0,
            Action = "RefundInitiated",
            Reason = req.RefundReason,
            PerformedBy = User.Identity?.Name ?? "admin",
            Notes = $"PaymentItemId={req.PaymentItemId}, Amount={req.RefundAmount}",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return Ok(new
        {
            id = refund.RefundId.ToString(),
            refundStatus = refund.RefundStatus,
            refundAmount = refund.RefundAmount,
            gatewayRefundId = refund.GatewayRefundId
        });
    }

    // ── GET /api/registrations/export  ── admin ─────────────────────────────
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

    // ── GET /api/registrations/stats  ── admin ──────────────────────────────
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

    // ── GET /api/registrations/:id/receipt  ── public ─────────────────────────
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

    // ── Load helper ──────────────────────────────────────────────────────────
    private Task<EventRegistration?> LoadReg(int id) =>
        _db.EventRegistrations
            .Include(r => r.ParticipantGroups).ThenInclude(g => g.Participants)
            .ThenInclude(p => p.CustomFieldValues)
            .Include(r => r.Payments).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(r => r.RegistrationId == id);

    // ── Status code translation helpers ──────────────────────────────────────
    // DB stores short codes; the frontend TypeScript types use long names.
    // All translation is centralised here so no other file needs to change.

    private static string PayStatusToDb(string s) => s switch
    {
        "Success"           => "S",
        "Pending"           => "P",
        "PartiallyRefunded" => "PR",
        "FullyRefunded"     => "FR",
        "Failed"            => "F",
        "Cancelled"         => "X",
        _                   => s    // already a short code — pass through
    };

    // ── Map helpers ──────────────────────────────────────────────────────────
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
