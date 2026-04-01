using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_API.Services;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/fixtures"), Authorize(Roles = "superadmin,eventadmin")]
public class FixturesController : ControllerBase
{
    private readonly TRSDbContext _db;
    private readonly AuthService _auth;
    public FixturesController(TRSDbContext db, AuthService auth) => (_db, _auth) = (db, auth);

    // GET /api/fixtures/status?programIds=1,2,3
    // Returns { "1": true, "2": false, ... } — true means a fixture row exists in the DB.
    // Used by the Fixtures table on mount to show Draw/Results status badges without
    // loading every full bracket JSON.
    // NOTE: this route must be declared BEFORE the {eventId}/{programId} route so ASP.NET
    // Core does not try to bind "status" as an int for eventId.
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] string? programIds)
    {
        if (string.IsNullOrWhiteSpace(programIds))
            return Ok(new Dictionary<string, bool>());

        var ids = programIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (!ids.Any())
            return Ok(new Dictionary<string, bool>());

        var existing = await _db.Fixtures
            .Where(f => ids.Contains(f.ProgramId))
            .Select(f => f.ProgramId)
            .ToListAsync();

        // Return string keys to match the frontend Record<string, boolean> type
        var result = ids.ToDictionary(id => id.ToString(), id => existing.Contains(id));
        return Ok(result);
    }

    // GET /api/fixtures/:eventId/:programId
    [HttpGet("{eventId:int}/{programId:int}")]
    public async Task<IActionResult> Get(int eventId, int programId)
    {
        var f = await _db.Fixtures.FirstOrDefaultAsync(x => x.EventId == eventId && x.ProgramId == programId);
        if (f == null) return Ok(null);
        return Ok(new
        {
            eventId,
            programId,
            f.FixtureMode,
            f.FixtureFormat,
            f.IsLocked,
            f.Phase,
            bracketStateJson = f.BracketStateJson,
            f.UpdatedAt
        });
    }

    // POST /api/fixtures/:eventId/:programId  — create or overwrite
    [HttpPost("{eventId:int}/{programId:int}")]
    public async Task<IActionResult> Save(int eventId, int programId, [FromBody] SaveFixtureRequest req)
    {
        var f = await _db.Fixtures.FirstOrDefaultAsync(x => x.EventId == eventId && x.ProgramId == programId);
        if (f == null)
        {
            f = new Fixture
            {
                EventId = eventId,
                ProgramId = programId,
                CreatedAt = DateTime.UtcNow,
                GeneratedBy = _auth.GetUserId(User)
            };
            _db.Fixtures.Add(f);
        }
        f.BracketStateJson = req.BracketStateJson;
        f.FixtureFormat = req.FixtureFormat;
        f.Phase = req.Phase;
        f.IsLocked = req.IsLocked;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { eventId, programId, f.FixtureFormat, f.IsLocked, f.Phase });
    }

    // DELETE /api/fixtures/:eventId/:programId  — reset fixture
    [HttpDelete("{eventId:int}/{programId:int}")]
    public async Task<IActionResult> Delete(int eventId, int programId)
    {
        var f = await _db.Fixtures.FirstOrDefaultAsync(x => x.EventId == eventId && x.ProgramId == programId);
        if (f != null) { _db.Fixtures.Remove(f); await _db.SaveChangesAsync(); }
        return Ok();
    }
}