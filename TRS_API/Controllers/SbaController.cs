using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/sba")]
public class SbaController : ControllerBase
{
    private readonly TRSDbContext _db;
    public SbaController(TRSDbContext db) => _db = db;

    // GET /api/sba/rankings  — public
    [HttpGet("rankings")]
    public async Task<IActionResult> GetRankings([FromQuery] string? gender)
    {
        var q = _db.SbaRankings.AsQueryable();
        if (!string.IsNullOrEmpty(gender)) q = q.Where(r => r.Gender == gender);
        var rows = await q.OrderBy(r => r.Ranking)
            .Select(r => new { r.SbaId, r.Name, r.Club, r.AccumulatedScore, r.Ranking })
            .ToListAsync();
        return Ok(rows);
    }

    // GET /api/sba/members/:id  — public
    [HttpGet("members/{sbaId}")]
    public async Task<IActionResult> GetMember(string sbaId)
    {
        var r = await _db.SbaRankings.FindAsync(sbaId);
        if (r == null) return NotFound(new { code = "NOT_FOUND", message = "SBA member not found." });
        return Ok(new {
            sbaId  = r.SbaId,
            name   = r.Name,
            club   = r.Club,
            dob    = r.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
            gender = r.Gender ?? "",
        });
    }

    // GET /api/sba/members?name=  — public
    [HttpGet("members")]
    public async Task<IActionResult> SearchMembers([FromQuery] string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Ok(new List<object>());
        var rows = await _db.SbaRankings
            .Where(r => r.Name.Contains(name))
            .OrderBy(r => r.Ranking)
            .Take(20)
            .Select(r => new { r.SbaId, r.Name, r.Club, r.AccumulatedScore, r.Ranking })
            .ToListAsync();
        return Ok(rows);
    }
}
