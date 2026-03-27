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

    // GET /api/fixtures/status?programIds=1,2,3  — bulk existence check for dashboard/table
    // Returns a dict of programId -> bool (true = fixture exists)
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] string programIds)
    {
        var ids = programIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
            .Where(n => n > 0).ToList();
        if (!ids.Any()) return Ok(new Dictionary<string, bool>());
        var existing = await _db.Fixtures
            .Where(f => ids.Contains(f.ProgramId))
            .Select(f => f.ProgramId)
            .ToListAsync();
        var result = ids.ToDictionary(id => id.ToString(), id => existing.Contains(id));
        return Ok(result);
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