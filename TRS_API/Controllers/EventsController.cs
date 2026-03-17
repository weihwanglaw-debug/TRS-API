using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly TRSDbContext _db;
    public EventsController(TRSDbContext db) => _db = db;

    // GET /api/events  — public
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var events = await LoadEvents().Where(e => e.IsActive)
            .OrderByDescending(e => e.EventStartDate).ToListAsync();
        var counts = await GetParticipantCounts(events.SelectMany(e => e.Programs.Select(p => p.ProgramId)).ToList());
        return Ok(events.Select(e => MapEvent(e, counts)));
    }

    // GET /api/events/:id  — public
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var ev = await LoadEvents().FirstOrDefaultAsync(e => e.EventId == id && e.IsActive);
        if (ev == null) return NotFound(new { code = "NOT_FOUND", message = "Event not found." });
        var counts = await GetParticipantCounts(ev.Programs.Select(p => p.ProgramId).ToList());
        return Ok(MapEvent(ev, counts));
    }

    // POST /api/events  — admin
    [HttpPost, Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> Create([FromBody] UpsertEventRequest req)
    {
        var ev = ApplyEventFields(new Event { CreatedAt = DateTime.UtcNow, IsActive = true }, req);
        _db.Events.Add(ev);
        await _db.SaveChangesAsync();
        return await GetById(ev.EventId);
    }

    // PUT /api/events/:id  — admin
    [HttpPut("{id:int}"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> Update(int id, [FromBody] UpsertEventRequest req)
    {
        var ev = await _db.Events.Include(e => e.GalleryImages).FirstOrDefaultAsync(e => e.EventId == id);
        if (ev == null) return NotFound(new { code = "NOT_FOUND", message = "Event not found." });
        ev.GalleryImages.Clear();
        ApplyEventFields(ev, req);
        ev.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetById(id);
    }

    // DELETE /api/events/:id  — admin
    [HttpDelete("{id:int}"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> Delete(int id)
    {
        var ev = await _db.Events.FindAsync(id);
        if (ev == null) return NotFound(new { code = "NOT_FOUND", message = "Event not found." });
        ev.IsActive = false; ev.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // POST /api/events/:id/programs  — admin
    [HttpPost("{id:int}/programs"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> AddProgram(int id, [FromBody] UpsertProgramRequest req)
    {
        if (!await _db.Events.AnyAsync(e => e.EventId == id))
            return NotFound(new { code = "NOT_FOUND", message = "Event not found." });
        var prog = ApplyProgramFields(new TrsProgram { EventId = id, CreatedAt = DateTime.UtcNow, IsActive = true }, req);
        _db.Programs.Add(prog);
        await _db.SaveChangesAsync();
        return Ok(MapProgram(prog, 0));
    }

    // PUT /api/events/:eid/programs/:pid  — admin
    [HttpPut("{eid:int}/programs/{pid:int}"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> UpdateProgram(int eid, int pid, [FromBody] UpsertProgramRequest req)
    {
        var prog = await _db.Programs.Include(p => p.Fields).Include(p => p.CustomFields)
            .FirstOrDefaultAsync(p => p.ProgramId == pid && p.EventId == eid);
        if (prog == null) return NotFound(new { code = "NOT_FOUND", message = "Program not found." });
        prog.CustomFields.Clear();
        ApplyProgramFields(prog, req);
        prog.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(MapProgram(prog, 0));
    }

    // DELETE /api/events/:eid/programs/:pid  — admin
    [HttpDelete("{eid:int}/programs/{pid:int}"), Authorize(Roles = "superadmin,eventadmin")]
    public async Task<IActionResult> DeleteProgram(int eid, int pid)
    {
        var prog = await _db.Programs.FirstOrDefaultAsync(p => p.ProgramId == pid && p.EventId == eid);
        if (prog == null) return NotFound(new { code = "NOT_FOUND", message = "Program not found." });
        prog.IsActive = false; prog.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private IQueryable<Event> LoadEvents() =>
        _db.Events
            .Include(e => e.Programs.Where(p => p.IsActive)).ThenInclude(p => p.Fields)
            .Include(e => e.Programs.Where(p => p.IsActive)).ThenInclude(p => p.CustomFields)
            .Include(e => e.GalleryImages);

    private async Task<Dictionary<int, int>> GetParticipantCounts(List<int> programIds)
    {
        if (!programIds.Any()) return new();
        return await _db.ParticipantGroups
            .Where(g => programIds.Contains(g.ProgramId) && g.GroupStatus != "Cancelled")
            .GroupBy(g => g.ProgramId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    private static Event ApplyEventFields(Event ev, UpsertEventRequest r)
    {
        ev.Name = r.Name; ev.Description = r.Description; ev.Venue = r.Venue;
        ev.VenueAddress = r.VenueAddress; ev.BannerUrl = r.BannerUrl; ev.ProspectusUrl = r.ProspectusUrl;
        ev.EventStartDate = DateOnly.Parse(r.EventStartDate);
        ev.EventEndDate = r.EventEndDate != null ? DateOnly.Parse(r.EventEndDate) : null;
        ev.OpenDate = DateOnly.Parse(r.OpenDate); ev.CloseDate = DateOnly.Parse(r.CloseDate);
        ev.MaxParticipants = r.MaxParticipants; ev.SponsorInfo = r.SponsorInfo;
        ev.ConsentStatement = r.ConsentStatement; ev.IsSports = r.IsSports;
        ev.SportType = r.SportType; ev.FixtureMode = r.FixtureMode;
        ev.GalleryImages = r.GalleryUrls.Select((url, i) =>
            new EventGalleryImage { ImageUrl = url, SortOrder = i }).ToList();
        return ev;
    }

    private static TrsProgram ApplyProgramFields(TrsProgram p, UpsertProgramRequest r)
    {
        p.Name = r.Name; p.Type = r.Type; p.MinAge = r.MinAge; p.MaxAge = r.MaxAge;
        p.Gender = r.Gender; p.Fee = r.Fee; p.PaymentRequired = r.PaymentRequired;
        p.FeeStructure = r.FeeStructure; p.SbaRequired = r.SbaRequired;
        p.MinPlayers = r.MinPlayers; p.MaxPlayers = r.MaxPlayers;
        p.MinParticipants = r.MinParticipants; p.MaxParticipants = r.MaxParticipants;
        if (p.Fields != null)
        {
            p.Fields.EnableSbaId = r.Fields.EnableSbaId; p.Fields.EnableDocumentUpload = r.Fields.EnableDocumentUpload;
            p.Fields.EnableGuardianInfo = r.Fields.EnableGuardianInfo; p.Fields.EnableRemark = r.Fields.EnableRemark;
        }
        else
        {
            p.Fields = new ProgramField
            {
                EnableSbaId = r.Fields.EnableSbaId,
                EnableDocumentUpload = r.Fields.EnableDocumentUpload,
                EnableGuardianInfo = r.Fields.EnableGuardianInfo,
                EnableRemark = r.Fields.EnableRemark
            };
        }
        p.CustomFields = r.Fields.CustomFields.Select((cf, i) => new ProgramCustomField
        {
            Label = cf.Label,
            FieldType = cf.FieldType,
            IsRequired = cf.IsRequired,
            Options = cf.Options,
            SortOrder = i
        }).ToList();
        return p;
    }

    private static object MapProgram(TrsProgram p, int currentParticipants) => new
    {
        id = p.ProgramId.ToString(),
        p.Name,
        p.Type,
        p.MinAge,
        p.MaxAge,
        p.Gender,
        p.Fee,
        p.PaymentRequired,
        p.FeeStructure,
        p.SbaRequired,
        p.MinPlayers,
        p.MaxPlayers,
        p.MinParticipants,
        p.MaxParticipants,
        currentParticipants,
        p.Status,
        participantSeeds = new List<object>(),
        fields = p.Fields == null ? (object)new
        {
            enableSbaId = false,
            enableDocumentUpload = false,
            enableGuardianInfo = false,
            enableRemark = false,
            customFields = new List<object>()
        }
        : new
        {
            enableSbaId = p.Fields.EnableSbaId,
            enableDocumentUpload = p.Fields.EnableDocumentUpload,
            enableGuardianInfo = p.Fields.EnableGuardianInfo,
            enableRemark = p.Fields.EnableRemark,
            customFields = p.CustomFields.OrderBy(cf => cf.SortOrder).Select(cf => (object)new
            {
                label = cf.Label,
                type = cf.FieldType,
                required = cf.IsRequired,
                options = cf.Options
            }).ToList()
        }
    };

    private static object MapEvent(Event ev, Dictionary<int, int> counts) => new
    {
        id = ev.EventId.ToString(),
        ev.Name,
        ev.Description,
        ev.Venue,
        ev.VenueAddress,
        bannerUrl = ev.BannerUrl ?? "",
        prospectusUrl = ev.ProspectusUrl ?? "",
        galleryUrls = ev.GalleryImages.OrderBy(g => g.SortOrder).Select(g => g.ImageUrl).ToList(),
        eventStartDate = ev.EventStartDate.ToString("yyyy-MM-dd"),
        eventEndDate = ev.EventEndDate?.ToString("yyyy-MM-dd") ?? "",
        openDate = ev.OpenDate.ToString("yyyy-MM-dd"),
        closeDate = ev.CloseDate.ToString("yyyy-MM-dd"),
        ev.MaxParticipants,
        sponsorInfo = ev.SponsorInfo ?? "",
        consentStatement = ev.ConsentStatement ?? "",
        ev.IsSports,
        sportType = ev.SportType ?? "",
        ev.FixtureMode,
        programs = ev.Programs.Where(p => p.IsActive)
            .Select(p => MapProgram(p, counts.GetValueOrDefault(p.ProgramId, 0))).ToList()
    };
}