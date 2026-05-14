using Microsoft.AspNetCore.Mvc;

namespace TRS_API.Controllers;

[ApiController, Route("api/uploads")]
public class UploadsController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UploadsController> _logger;

    // Allowed MIME types → extension
    private static readonly Dictionary<string, string> AllowedTypes = new()
    {
        { "image/jpeg",       ".jpg"  },
        { "image/png",        ".png"  },
        { "image/webp",       ".webp" },
        { "application/pdf",  ".pdf"  },
    };

    private const long MaxImageBytes = 2  * 1024 * 1024;  // 2 MB
    private const long MaxPdfBytes   = 8  * 1024 * 1024;  // 8 MB

    public UploadsController(IWebHostEnvironment env, ILogger<UploadsController> logger)
    {
        _env    = env;
        _logger = logger;
    }

    // POST /api/uploads
    // Form fields:
    //   file   — the file to upload (required)
    //   folder — sub-folder under uploads/ e.g. "events/gallery" (optional)
    [HttpPost]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? folder)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { code = "NO_FILE", message = "No file provided." });

        // Validate MIME type
        if (!AllowedTypes.TryGetValue(file.ContentType.ToLowerInvariant(), out var ext))
            return BadRequest(new { code = "INVALID_TYPE",
                message = "Only JPG, PNG, WEBP images and PDF files are accepted." });

        // Validate file size
        var maxBytes = file.ContentType.StartsWith("image/") ? MaxImageBytes : MaxPdfBytes;
        if (file.Length > maxBytes)
            return BadRequest(new { code = "FILE_TOO_LARGE",
                message = $"File exceeds the {maxBytes / 1024 / 1024} MB limit." });

        // Build destination path: wwwroot/uploads/<folder>/<year>/<month>/<guid><ext>
        var safeFolder  = SanitizeFolder(folder);   // e.g. "events/gallery"
        var datePart    = DateTime.UtcNow.ToString("yyyy/MM");
        var fileName    = $"{Guid.NewGuid()}{ext}";

        // Relative URL path that will be stored in the DB
        var relativePath = $"/uploads/{safeFolder}/{datePart}/{fileName}"
                           .Replace("//", "/");      // handle empty folder

        // Absolute path on disk inside wwwroot
        var absolutePath = Path.Combine(
            _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            "uploads",
            safeFolder.Replace("/", Path.DirectorySeparatorChar.ToString()),
            datePart.Replace("/", Path.DirectorySeparatorChar.ToString()),
            fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write);
        await file.CopyToAsync(stream);

        _logger.LogInformation("Upload saved: {Path}", absolutePath);

        // Return the relative path — the DB stores this value
        return Ok(new { path = relativePath });
    }

    // Strip anything dangerous from the folder name (no "..", no absolute paths)
    private static string SanitizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "general";
        var safe = folder.Replace("\\", "/")
                         .Trim('/')
                         .Replace("..", "")
                         .Trim('/');
        return string.IsNullOrWhiteSpace(safe) ? "general" : safe;
    }
}
