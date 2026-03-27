using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TRS_API.BackgroundJobs;
using TRS_API.Services;
using TRS_Data.Models;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    // Automatically return 400 ValidationProblem for any [Required], [EmailAddress],
    // [MinLength], [Range] etc. annotation failures — no manual ModelState checks needed.
    options.Filters.Add<TRS_API.Filters.ValidateModelFilter>();
})
.AddJsonOptions(options =>
{
    // Ensure all JSON responses use camelCase to match TypeScript frontend expectations.
    // This covers shorthand property serialization (e.g. p.FullName -> "fullName").
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.DictionaryKeyPolicy  = System.Text.Json.JsonNamingPolicy.CamelCase;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => {
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement {
        { new Microsoft.OpenApi.Models.OpenApiSecurityScheme {
              Reference = new Microsoft.OpenApi.Models.OpenApiReference {
                  Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }},
          Array.Empty<string>() }
    });
});

// Database
builder.Services.AddDbContext<TRSDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("TRSConnection")));

// JWT Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            // Map ClaimTypes.Role so [Authorize(Roles=...)] works correctly
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(2),  // small tolerance for clock drift
        };
    });
builder.Services.AddAuthorization();

// App services
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddHostedService<PaymentCleanupWorker>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<ReceiptService>();

// CORS
builder.Services.AddCors(options => {
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? new[] { "http://localhost:5173", "http://localhost:3000" };
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// Rate Limiting
builder.Services.AddRateLimiter(options =>
    options.AddFixedWindowLimiter("payment", opt => {
        opt.Window = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("RateLimiting:WindowMinutes", 1));
        opt.PermitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 5);
    }));

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Security headers
app.Use(async (ctx, next) => {
    var isDev = app.Environment.IsDevelopment();
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' https://js.stripe.com; " +
        "frame-src https://js.stripe.com; " +
        (isDev ? "connect-src 'self' https://localhost:7183 https://*.stripe.com;"
               : "connect-src 'self' https://*.stripe.com;");
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    await next();
});

app.UseCors("AllowFrontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();  // serves TRS_API/wwwroot/** at root URL
app.MapControllers();
app.Run();