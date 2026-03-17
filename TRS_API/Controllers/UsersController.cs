using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_API.Services;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/admin/users"), Authorize(Roles = "superadmin")]
public class UsersController : ControllerBase
{
    private readonly TRSDbContext _db;
    private readonly AuthService _auth;

    public UsersController(TRSDbContext db, AuthService auth) => (_db, _auth) = (db, auth);

    // GET /api/admin/users
    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.AdminUsers.Where(u => u.IsActive)
            .Select(u => new {
                id = u.UserId.ToString(),
                u.Email,
                u.Name,
                u.Role,
                lastLogin = u.LastLogin,
                u.MustChangePassword
            })
            .ToListAsync());

    // POST /api/admin/users
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        if (await _db.AdminUsers.AnyAsync(u => u.Email == req.Email))
            return BadRequest(new { code = "EMAIL_TAKEN", message = "Email already in use." });

        var user = new AdminUser
        {
            Email = req.Email,
            Name = req.Name,
            Role = req.Role,
            PasswordHash = _auth.HashPassword(req.Password),
            MustChangePassword = req.MustChangePassword,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AdminUsers.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new { id = user.UserId.ToString(), user.Email, user.Name, user.Role });
    }

    // PUT /api/admin/users/:id
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest req)
    {
        var user = await _db.AdminUsers.FindAsync(id);
        if (user == null || !user.IsActive)
            return NotFound(new { code = "NOT_FOUND", message = "User not found." });

        if (req.Email != null && req.Email != user.Email)
        {
            if (await _db.AdminUsers.AnyAsync(u => u.Email == req.Email && u.UserId != id))
                return BadRequest(new { code = "EMAIL_TAKEN", message = "Email already in use." });
            user.Email = req.Email;
        }
        if (req.Name != null) user.Name = req.Name;
        if (req.Role != null) user.Role = req.Role;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = user.UserId.ToString(), user.Email, user.Name, user.Role });
    }

    // DELETE /api/admin/users/:id  — soft delete
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int currentUserId)
    {
        if (id == currentUserId)
            return BadRequest(new { code = "CANNOT_DELETE_SELF", message = "Cannot delete your own account." });
        var user = await _db.AdminUsers.FindAsync(id);
        if (user == null) return NotFound(new { code = "NOT_FOUND", message = "User not found." });
        user.IsActive = false; user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // POST /api/admin/users/:id/reset-password
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        var user = await _db.AdminUsers.FindAsync(id);
        if (user == null) return NotFound(new { code = "NOT_FOUND", message = "User not found." });
        user.PasswordHash = _auth.HashPassword(req.NewPassword);
        user.MustChangePassword = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }
}