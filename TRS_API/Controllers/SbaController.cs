using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TRS_Data.Models;

namespace TRS_API.Controllers;

[ApiController, Route("api/sba")]
public class SbaController : ControllerBase
{
    private static readonly string[] RankingTypes =
    [
        "Men's Singles", "Women's Singles", "Men's Doubles", "Women's Doubles", "Mixed Doubles",
        "U19 Boys Singles", "U19 Girls Singles", "U19 Boys Doubles", "U19 Girls Doubles", "U19 Mixed Doubles",
        "U17 Boys Singles", "U17 Girls Singles", "U17 Boys Doubles", "U17 Girls Doubles", "U17 Mixed Doubles",
        "U15 Boys Singles", "U15 Girls Singles", "U15 Boys Doubles", "U15 Girls Doubles", "U15 Mixed Doubles",
        "U13 Boys Singles", "U13 Girls Singles", "U13 Boys Doubles", "U13 Girls Doubles", "U13 Mixed Doubles",
        "U11 Boys Singles", "U11 Girls Singles", "U11 Boys Doubles", "U11 Girls Doubles", "U11 Mixed Doubles",
        "U9 Boys Singles", "U9 Girls Singles", "U9 Boys Doubles", "U9 Girls Doubles", "U9 Mixed Doubles",
    ];

    private static readonly Dictionary<string, string> TypeByNormalizedName =
        RankingTypes.ToDictionary(NormalizeType, t => t);

    private readonly TRSDbContext _db;
    public SbaController(TRSDbContext db) => _db = db;

    [HttpGet("types")]
    public IActionResult GetTypes() => Ok(RankingTypes.Select(t => new
    {
        value = t,
        label = t,
        players = IsDoubles(t) ? 2 : 1,
        gender = InferGender(t),
        minAge = InferMinAge(t),
        maxAge = InferMaxAge(t),
    }));

    [HttpGet("rankings")]
    public async Task<IActionResult> GetRankings([FromQuery] string? type)
    {
        var q = _db.SbaRankings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(type))
        {
            var rankingType = ResolveType(type);
            if (rankingType == null) return BadRequest(new { code = "INVALID_TYPE", message = "Unknown SBA ranking type." });
            q = q.Where(r => r.RankingType == rankingType);
        }

        var rows = await q.OrderBy(r => r.RankingType).ThenBy(r => r.Ranking).ToListAsync();
        return Ok(rows.Select(MapRanking));
    }

    [HttpGet("members/{sbaId}")]
    public async Task<IActionResult> GetMember(string sbaId, [FromQuery] string? type)
    {
        var normalizedId = sbaId.Trim().ToUpperInvariant();
        var q = _db.SbaRankings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(type))
        {
            var rankingType = ResolveType(type);
            if (rankingType == null) return BadRequest(new { code = "INVALID_TYPE", message = "Unknown SBA ranking type." });
            q = q.Where(r => r.RankingType == rankingType);
        }

        var r = await q
            .Where(x => x.Player1SbaId == normalizedId || x.Player2SbaId == normalizedId)
            .OrderBy(x => x.Ranking)
            .FirstOrDefaultAsync();
        if (r == null) return NotFound(new { code = "NOT_FOUND", message = "SBA member not found." });

        var isP2 = r.Player2SbaId == normalizedId;
        return Ok(new
        {
            sbaId = isP2 ? r.Player2SbaId : r.Player1SbaId,
            name = isP2 ? r.Player2Name : r.Player1Name,
            club = isP2 ? r.Player2Club : r.Player1Club,
            dob = (isP2 ? r.Player2DateOfBirth : r.Player1DateOfBirth)?.ToString("yyyy-MM-dd") ?? "",
            rankingType = r.RankingType,
            ranking = r.Ranking,
            accumulatedScore = r.AccumulatedScore,
        });
    }

    [HttpGet("members")]
    public async Task<IActionResult> SearchMembers([FromQuery] string? name, [FromQuery] string? type)
    {
        if (string.IsNullOrWhiteSpace(name)) return Ok(new List<object>());
        var term = name.Trim();
        var q = _db.SbaRankings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(type))
        {
            var rankingType = ResolveType(type);
            if (rankingType == null) return BadRequest(new { code = "INVALID_TYPE", message = "Unknown SBA ranking type." });
            q = q.Where(r => r.RankingType == rankingType);
        }

        var rows = await q
            .Where(r => r.Player1Name.Contains(term) || (r.Player2Name != null && r.Player2Name.Contains(term)))
            .OrderBy(r => r.RankingType).ThenBy(r => r.Ranking)
            .Take(20)
            .ToListAsync();
        return Ok(rows.Select(MapRanking));
    }

    [HttpPost("import"), Authorize(Roles = "superadmin,eventadmin")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Import([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { code = "NO_FILE", message = "Please upload an .xlsx file." });
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "INVALID_FILE", message = "Only .xlsx files are supported." });

        await using var stream = file.OpenReadStream();
        var parsed = SbaWorkbookParser.Parse(stream, TypeByNormalizedName);
        if (parsed.Errors.Count > 0)
            return BadRequest(new { code = "IMPORT_FAILED", message = "SBA workbook contains invalid data.", parsed.Errors });

        var includedTypes = parsed.Rows.Select(r => r.RankingType).Distinct().ToList();
        if (includedTypes.Count == 0)
            return BadRequest(new { code = "NO_MATCHING_SHEETS", message = "No recognized SBA ranking sheets were found." });

        var existing = await _db.SbaRankings.Where(r => includedTypes.Contains(r.RankingType)).ToListAsync();
        _db.SbaRankings.RemoveRange(existing);
        _db.SbaRankings.AddRange(parsed.Rows);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            importedRows = parsed.Rows.Count,
            categories = includedTypes.OrderBy(t => Array.IndexOf(RankingTypes, t)).Select(t => new
            {
                rankingType = t,
                rows = parsed.Rows.Count(r => r.RankingType == t),
            }),
            skippedSheets = parsed.SkippedSheets,
        });
    }

    private static object MapRanking(SbaRanking r) => new
    {
        id = r.SbaRankingId,
        rankingType = r.RankingType,
        ranking = r.Ranking,
        accumulatedScore = r.AccumulatedScore,
        tournaments = r.Tournaments,
        yearOfBirth = r.YearOfBirth,
        player1 = new { sbaId = r.Player1SbaId, name = r.Player1Name, club = r.Player1Club, dob = r.Player1DateOfBirth?.ToString("yyyy-MM-dd") ?? "" },
        player2 = string.IsNullOrWhiteSpace(r.Player2SbaId)
            ? (object?)null
            : new { sbaId = r.Player2SbaId, name = r.Player2Name, club = r.Player2Club, dob = r.Player2DateOfBirth?.ToString("yyyy-MM-dd") ?? "" },
    };

    private static string? ResolveType(string type) =>
        TypeByNormalizedName.TryGetValue(NormalizeType(type), out var resolved) ? resolved : null;

    private static string NormalizeType(string value) =>
        string.Join(" ", value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool IsDoubles(string type) => type.Contains("Doubles", StringComparison.OrdinalIgnoreCase);
    private static string InferGender(string type)
    {
        if (type.Contains("Mixed", StringComparison.OrdinalIgnoreCase)) return "Mixed";
        if (type.Contains("Women's", StringComparison.OrdinalIgnoreCase) || type.Contains("Girls", StringComparison.OrdinalIgnoreCase)) return "Female";
        if (type.Contains("Men's", StringComparison.OrdinalIgnoreCase) || type.Contains("Boys", StringComparison.OrdinalIgnoreCase)) return "Male";
        return "Open";
    }
    private static int InferMinAge(string type) => 0;
    private static int InferMaxAge(string type)
    {
        foreach (var age in new[] { 19, 17, 15, 13, 11, 9 })
            if (type.StartsWith($"U{age} ", StringComparison.OrdinalIgnoreCase)) return age - 1;
        return 99;
    }

    private sealed record ParsedWorkbook(List<SbaRanking> Rows, List<string> SkippedSheets, List<string> Errors);

    private static class SbaWorkbookParser
    {
        private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static ParsedWorkbook Parse(Stream stream, Dictionary<string, string> knownTypes)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var sharedStrings = ReadSharedStrings(archive);
            var sheets = ReadSheets(archive);
            var rows = new List<SbaRanking>();
            var skipped = new List<string>();
            var errors = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var sheet in sheets)
            {
                if (!knownTypes.TryGetValue(NormalizeType(sheet.Name), out var rankingType))
                {
                    skipped.Add(sheet.Name.Trim());
                    continue;
                }
                var entry = archive.GetEntry(sheet.Path);
                if (entry == null)
                {
                    errors.Add($"{sheet.Name.Trim()}: worksheet XML was not found.");
                    continue;
                }

                var isDoubles = IsDoubles(rankingType);
                foreach (var row in ReadRows(entry, sharedStrings).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(Get(row, 0))) continue;
                    try
                    {
                        rows.Add(isDoubles
                            ? ParseDoublesRow(row, rankingType, now)
                            : ParseSinglesRow(row, rankingType, now));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{sheet.Name.Trim()} row {row.RowNumber}: {ex.Message}");
                    }
                }
            }

            return new ParsedWorkbook(rows, skipped, errors);
        }

        private static SbaRanking ParseSinglesRow(SheetRow row, string rankingType, DateTime now) => new()
        {
            RankingType = rankingType,
            Ranking = ToInt(Get(row, 0), "Rank"),
            Player1Name = Required(Get(row, 1), "Player"),
            Player1SbaId = Required(Get(row, 2), "Member ID").ToUpperInvariant(),
            YearOfBirth = ToNullableInt(Get(row, 3)),
            AccumulatedScore = ToInt(Get(row, 4), "Points"),
            Tournaments = ToInt(Get(row, 5), "Tournaments"),
            Player1Club = CleanClub(Get(row, 6)),
            Player1DateOfBirth = ToDate(Get(row, 7)),
            UpdatedAt = now,
        };

        private static SbaRanking ParseDoublesRow(SheetRow row, string rankingType, DateTime now) => new()
        {
            RankingType = rankingType,
            Ranking = ToInt(Get(row, 0), "Rank"),
            Player1Name = Required(Get(row, 1), "Player1"),
            Player2Name = Required(Get(row, 2), "Player2"),
            Player1SbaId = Required(Get(row, 3), "Member ID1").ToUpperInvariant(),
            Player2SbaId = Required(Get(row, 4), "Member ID2").ToUpperInvariant(),
            YearOfBirth = ToNullableInt(Get(row, 5)),
            AccumulatedScore = ToInt(Get(row, 6), "Points"),
            Tournaments = ToInt(Get(row, 7), "Tournaments"),
            Player1Club = CleanClub(Get(row, 8)),
            Player2Club = CleanClub(Get(row, 9)),
            Player1DateOfBirth = ToDate(Get(row, 10)),
            Player2DateOfBirth = ToDate(Get(row, 11)),
            UpdatedAt = now,
        };

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return [];
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            return doc.Descendants(Ns + "si")
                .Select(si => string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)))
                .ToList();
        }

        private static List<(string Name, string Path)> ReadSheets(ZipArchive archive)
        {
            using var workbookStream = archive.GetEntry("xl/workbook.xml")!.Open();
            using var relsStream = archive.GetEntry("xl/_rels/workbook.xml.rels")!.Open();
            var workbook = XDocument.Load(workbookStream);
            var rels = XDocument.Load(relsStream).Root!.Elements(PackageRelNs + "Relationship")
                .ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value);

            return workbook.Descendants(Ns + "sheet")
                .Select(s =>
                {
                    var relId = s.Attribute(RelNs + "id")!.Value;
                    var target = rels[relId].Replace("\\", "/");
                    var path = target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : $"xl/{target}";
                    return (Name: s.Attribute("name")!.Value, Path: path);
                })
                .ToList();
        }

        private static IEnumerable<SheetRow> ReadRows(ZipArchiveEntry entry, List<string> sharedStrings)
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            foreach (var row in doc.Descendants(Ns + "row"))
            {
                var values = new Dictionary<int, string>();
                foreach (var c in row.Elements(Ns + "c"))
                {
                    var refAttr = c.Attribute("r")?.Value ?? "";
                    var col = ColumnIndex(refAttr);
                    if (col < 0) continue;
                    var raw = c.Element(Ns + "v")?.Value ?? "";
                    var type = c.Attribute("t")?.Value;
                    values[col] = type == "s" && int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Count
                        ? sharedStrings[idx]
                        : raw;
                }
                yield return new SheetRow(int.Parse(row.Attribute("r")?.Value ?? "0"), values);
            }
        }

        private static int ColumnIndex(string cellRef)
        {
            var letters = new string(cellRef.TakeWhile(char.IsLetter).ToArray());
            if (letters.Length == 0) return -1;
            var idx = 0;
            foreach (var ch in letters.ToUpperInvariant()) idx = idx * 26 + (ch - 'A' + 1);
            return idx - 1;
        }

        private static string Get(SheetRow row, int index) =>
            row.Values.TryGetValue(index, out var v) ? v.Trim() : "";

        private static string Required(string value, string field) =>
            string.IsNullOrWhiteSpace(value) ? throw new FormatException($"{field} is required.") : value.Trim();

        private static int ToInt(string value, string field) =>
            int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new FormatException($"{field} must be a number.");

        private static int? ToNullableInt(string value) =>
            int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;

        private static DateOnly? ToDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
                return DateOnly.FromDateTime(DateTime.FromOADate(serial));
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
            if (DateTime.TryParseExact(value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return DateOnly.FromDateTime(dt);
            throw new FormatException($"Invalid DOB value '{value}'.");
        }

        private static string? CleanClub(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed record SheetRow(int RowNumber, Dictionary<int, string> Values);
    }
}
