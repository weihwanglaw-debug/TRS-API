using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TRS_Data.Models;

namespace TRS_API.Services;

public class AuthService
{
    private readonly IConfiguration _config;
    public AuthService(IConfiguration config) => _config = config;

    public string HashPassword(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    public bool VerifyPassword(string plain, string hash) =>
        BCrypt.Net.BCrypt.Verify(plain, hash);

    public string GenerateJwt(AdminUser user)
    {
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role,               user.Role),
            new Claim("name",                        user.Name),
            new Claim("mustChangePassword",          user.MustChangePassword.ToString().ToLower()),
        };
        var token = new JwtSecurityToken(
            issuer:             _config["Jwt:Issuer"],
            audience:           _config["Jwt:Audience"],
            claims:             claims,
            expires:            DateTime.UtcNow.AddHours(_config.GetValue<int>("Jwt:ExpiryHours", 8)),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public int GetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        var val = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(val, out var id) ? id : 0;
    }
}
