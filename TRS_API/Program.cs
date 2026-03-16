using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System;
using TRS_API.BackgroundJobs;
using TRS_API.Services;
using TRS_Data;
using TRS_Data.Models;

var builder = WebApplication.CreateBuilder(args);

// ===========================
// SERVICES
// ===========================
builder.Services.AddControllers();

builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<ReceiptService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<PaymentCleanupWorker>();

// CORS
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? new[] { "http://localhost:3000" };

    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Rate Limiting
var rateLimitWindow = builder.Configuration.GetValue<int>("RateLimiting:WindowMinutes", 1);
var rateLimitPermits = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 5);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("payment", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(rateLimitWindow);
        opt.PermitLimit = rateLimitPermits;
    });
});

// Database
builder.Services.AddDbContext<TRSDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("TRSConnection")));

var app = builder.Build();

// ===========================
// MIDDLEWARE
// ===========================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Security Headers
app.Use(async (context, next) =>
{
    var isDev = app.Environment.IsDevelopment();

    var connectSrc = isDev
        ? "connect-src 'self' https://localhost:7183 https://*.stripe.com;"
        : "connect-src 'self' https://api.yourdomain.com https://*.stripe.com;";

    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' https://js.stripe.com; " +
        "frame-src https://js.stripe.com; " +
        connectSrc;

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";

    await next();
});


app.UseCors("AllowFrontend");
app.UseRateLimiter();

// REMOVE THESE TWO LINES (no authentication configured):
// app.UseAuthentication(); 
// app.UseAuthorization();

app.MapControllers();

app.Run();