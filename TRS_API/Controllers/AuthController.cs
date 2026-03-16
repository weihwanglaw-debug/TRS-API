using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_API.Services;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly TRSDbContext _db;
    private readonly AuthService  _auth;
    private readonly ILogger<AuthController> _log;

    public AuthController(TRSDbContext db, AuthService auth, ILogger<AuthController> log)
        => (_db, _auth, _log) = (db, auth, log);

    // POST /api/auth/login  — public
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _db.AdminUsers
            .FirstOrDefaultAsync(u => u.Email == req.Email && u.IsActive);

        if (user == null || !_auth.VerifyPassword(req.Password, user.PasswordHash))
            return Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Invalid email or password." });

        user.LastLogin = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new {
            token = _auth.GenerateJwt(user),
            user  = MapUser(user)
        });
    }

    // POST /api/auth/logout  — token is stateless; client discards it
    [HttpPost("logout")]
    public IActionResult Logout() => Ok();

    // GET /api/auth/me  — admin
    [HttpGet("me"), Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _db.AdminUsers.FindAsync(_auth.GetUserId(User));
        if (user == null || !user.IsActive) return Unauthorized();
        return Ok(MapUser(user));
    }

    // POST /api/auth/change-password  — admin
    [HttpPost("change-password"), Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var user = await _db.AdminUsers.FindAsync(_auth.GetUserId(User));
        if (user == null) return NotFound();

        if (!_auth.VerifyPassword(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { code = "INVALID_CREDENTIALS", message = "Current password is incorrect." });

        if (req.CurrentPassword == req.NewPassword)
            return BadRequest(new { code = "SAME_PASSWORD", message = "New password must differ from current." });

        user.PasswordHash       = _auth.HashPassword(req.NewPassword);
        user.MustChangePassword = false;
        user.UpdatedAt          = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static object MapUser(AdminUser u) => new {
        id                = u.UserId.ToString(),
        email             = u.Email,
        name              = u.Name,
        role              = u.Role,
        lastLogin         = u.LastLogin,
        mustChangePassword = u.MustChangePassword,
    };
}
