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

    // DELETE /api/fixtures/:eventId/:programId  — reset fixture
    [HttpDelete("{eventId:int}/{programId:int}")]
    public async Task<IActionResult> Delete(int eventId, int programId)
    {
        var f = await _db.Fixtures.FirstOrDefaultAsync(x => x.EventId == eventId && x.ProgramId == programId);
        if (f != null) { _db.Fixtures.Remove(f); await _db.SaveChangesAsync(); }
        return Ok();
    }
}