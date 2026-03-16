using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly TRSDbContext _db;
    public ConfigController(TRSDbContext db) => _db = db;

    // GET /api/config  — public
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var rows = await _db.SystemConfigs.ToListAsync();
        var dict = rows.ToDictionary(r => r.ConfigKey, r => r.ConfigValue);
        return Ok(new {
            appName       = dict.GetValueOrDefault("appName",       "TRS"),
            logoUrl       = dict.GetValueOrDefault("logoUrl",       ""),
            heroTitle     = dict.GetValueOrDefault("heroTitle",     ""),
            heroSubtitle  = dict.GetValueOrDefault("heroSubtitle",  ""),
            heroImageUrl  = dict.GetValueOrDefault("heroImageUrl",  ""),
            currency      = dict.GetValueOrDefault("currency",      "SGD"),
            contactEmail  = dict.GetValueOrDefault("contactEmail",  ""),
            copyrightText = dict.GetValueOrDefault("copyrightText", ""),
            consentText   = dict.GetValueOrDefault("consentText",   ""),
        });
    }

    // PUT /api/config  — admin
    [HttpPut, Authorize]
    public async Task<IActionResult> Update([FromBody] UpdateConfigRequest req)
    {
        foreach (var (key, value) in req.Updates)
        {
            var row = await _db.SystemConfigs.FindAsync(key);
            if (row != null) {
                row.ConfigValue = value;
                row.UpdatedAt   = DateTime.UtcNow;
            } else {
                _db.SystemConfigs.Add(new SystemConfig {
                    ConfigKey = key, ConfigValue = value, UpdatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();
        return await Get();
    }
}
