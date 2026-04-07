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
    private readonly FixtureGenerationService _fixtureGeneration;

    public FixturesController(TRSDbContext db, AuthService auth, FixtureGenerationService fixtureGeneration)
        => (_db, _auth, _fixtureGeneration) = (db, auth, fixtureGeneration);

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

        var result = ids.ToDictionary(id => id.ToString(), id => existing.Contains(id));
        return Ok(result);
    }

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
            f.UpdatedAt,
        });
    }

    [HttpPost("{eventId:int}/{programId:int}/generate")]
    public async Task<IActionResult> Generate(int eventId, int programId, [FromBody] GenerateFixtureRequest req)
    {
        var result = await _fixtureGeneration.GenerateAsync(eventId, programId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPost("{eventId:int}/{programId:int}/swap")]
    public async Task<IActionResult> Swap(int eventId, int programId, [FromBody] SwapFixtureTeamsRequest req)
    {
        var result = await _fixtureGeneration.SwapTeamsAsync(eventId, programId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPost("{eventId:int}/{programId:int}/advance-to-knockout")]
    public async Task<IActionResult> AdvanceToKnockout(int eventId, int programId)
    {
        var result = await _fixtureGeneration.AdvanceToKnockoutAsync(eventId, programId);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPost("{eventId:int}/{programId:int}/advance-round")]
    public async Task<IActionResult> AdvanceRound(int eventId, int programId)
    {
        var result = await _fixtureGeneration.AdvanceKnockoutRoundAsync(eventId, programId);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPatch("{eventId:int}/{programId:int}/score/{matchId}")]
    public async Task<IActionResult> SaveScore(int eventId, int programId, string matchId, [FromBody] SaveFixtureScoreRequest req)
    {
        var result = await _fixtureGeneration.SaveScoreAsync(eventId, programId, matchId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPatch("{eventId:int}/{programId:int}/schedule/{matchId}")]
    public async Task<IActionResult> UpdateSchedule(int eventId, int programId, string matchId, [FromBody] UpdateFixtureScheduleRequest req)
    {
        var result = await _fixtureGeneration.UpdateScheduleAsync(eventId, programId, matchId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPatch("{eventId:int}/{programId:int}/heats/result")]
    public async Task<IActionResult> SaveHeatResult(int eventId, int programId, [FromBody] SaveHeatResultRequest req)
    {
        var result = await _fixtureGeneration.SaveHeatResultAsync(eventId, programId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPost("{eventId:int}/{programId:int}/heats/advance")]
    public async Task<IActionResult> AdvanceHeatsRound(int eventId, int programId, [FromBody] AdvanceHeatsRoundRequest req)
    {
        var result = await _fixtureGeneration.AdvanceHeatsRoundAsync(eventId, programId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

    [HttpPost("{eventId:int}/{programId:int}/heats/places")]
    public async Task<IActionResult> AssignHeatPlaces(int eventId, int programId, [FromBody] AssignHeatPlacesRequest req)
    {
        var result = await _fixtureGeneration.AssignHeatPlacesAsync(eventId, programId, req);
        if (!result.Success)
            return BadRequest(new { code = result.Code, message = result.Message });

        return Ok(result.State);
    }

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
                GeneratedBy = _auth.GetUserId(User),
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

    [HttpDelete("{eventId:int}/{programId:int}")]
    public async Task<IActionResult> Delete(int eventId, int programId)
    {
        var f = await _db.Fixtures.FirstOrDefaultAsync(x => x.EventId == eventId && x.ProgramId == programId);
        if (f != null)
        {
            _db.Fixtures.Remove(f);
            await _db.SaveChangesAsync();
        }

        return Ok();
    }
}
