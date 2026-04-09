using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TRS_API.Models;
using TRS_Data.Models;

namespace TRS_API.Services;

public class FixtureGenerationService
{
    private readonly TRSDbContext _db;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FixtureGenerationService(TRSDbContext db) => _db = db;

    public async Task<FixtureGenerationResult> GenerateAsync(int eventId, int programId, GenerateFixtureRequest req)
    {
        var program = await _db.Programs
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.ProgramId == programId && p.EventId == eventId);
        if (program == null)
            return FixtureGenerationResult.Fail("PROGRAM_NOT_FOUND", "Program not found.");

        var groups = await _db.ParticipantGroups
            .Include(g => g.Participants)
            .Where(g => g.EventId == eventId && g.ProgramId == programId && g.GroupStatus != "Cancelled")
            .OrderBy(g => g.GroupId)
            .ToListAsync();

        if (groups.Count < 2)
            return FixtureGenerationResult.Fail("NOT_ENOUGH", "At least 2 registered entries are required.");

        var normalizedSeeds = NormalizeSeeds(groups, req.Seeds);
        if (!normalizedSeeds.Success)
            return normalizedSeeds;
        var seedEntries = normalizedSeeds.State!.Seeds;

        var config = NormalizeConfig(req.Config);
        if (!config.Success)
            return config;
        var fixtureConfig = config.State!.Config;

        FixtureState state;
        if (!string.IsNullOrWhiteSpace(req.PreviewBracketJson))
        {
            var preview = ValidatePreview(req.PreviewBracketJson!, seedEntries, fixtureConfig);
            if (!preview.Success)
                return preview;
            state = preview.State!;
        }
        else
        {
            state = GenerateState(seedEntries, fixtureConfig);
        }

        foreach (var group in groups)
        {
            var seed = seedEntries.First(s => s.Id == group.GroupId.ToString());
            group.Seed = seed.Seed;
            group.UpdatedAt = DateTime.UtcNow;
        }

        var fixture = await _db.Fixtures.FirstOrDefaultAsync(f => f.EventId == eventId && f.ProgramId == programId);
        if (fixture == null)
        {
            fixture = new Fixture
            {
                EventId = eventId,
                ProgramId = programId,
                FixtureMode = program.Event?.FixtureMode ?? "internal",
                CreatedAt = DateTime.UtcNow,
            };
            _db.Fixtures.Add(fixture);
        }

        var stateJson = JsonSerializer.Serialize(state, _jsonOptions);
        fixture.BracketStateJson = stateJson;
        fixture.FixtureFormat = state.Format;
        fixture.Phase = state.Phase;
        fixture.IsLocked = state.Locked;
        fixture.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return FixtureGenerationResult.Ok(state, stateJson);
    }

    public async Task<FixtureGenerationResult> SwapTeamsAsync(int eventId, int programId, SwapFixtureTeamsRequest req)
    {
        if (req.IdA == req.IdB)
            return FixtureGenerationResult.Fail("INVALID_SWAP", "Choose two different teams to swap.");

        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (IsLocked(state))
            return FixtureGenerationResult.Fail("LOCKED", "Cannot swap after results have been entered.");

        if (!state.Seeds.Any(s => s.Id == req.IdA) || !state.Seeds.Any(s => s.Id == req.IdB))
            return FixtureGenerationResult.Fail("INVALID_TEAM", "One or more teams could not be found.");

        var seedA = state.Seeds.First(s => s.Id == req.IdA).Seed;
        var seedB = state.Seeds.First(s => s.Id == req.IdB).Seed;
        foreach (var seed in state.Seeds)
        {
            if (seed.Id == req.IdA) seed.Seed = seedB;
            else if (seed.Id == req.IdB) seed.Seed = seedA;
        }

        var seedsById = state.Seeds.ToDictionary(s => s.Id, StringComparer.Ordinal);
        FixtureTeam SwapTeam(FixtureTeam t)
        {
            if (t.Id == req.IdA) return ToTeam(seedsById[req.IdB]);
            if (t.Id == req.IdB) return ToTeam(seedsById[req.IdA]);
            return t;
        }

        foreach (var group in state.Groups)
        {
            group.Teams = group.Teams.Select(SwapTeam).ToList();
            foreach (var match in group.Matches)
            {
                match.Team1 = SwapTeam(match.Team1);
                match.Team2 = SwapTeam(match.Team2);
            }
        }

        foreach (var match in state.Matches)
        {
            match.Team1 = SwapTeam(match.Team1);
            match.Team2 = SwapTeam(match.Team2);
        }

        if (state.HeatRounds != null)
        {
            foreach (var round in state.HeatRounds)
            {
                foreach (var result in round.Results)
                {
                    if (result.TeamId == req.IdA) result.TeamId = req.IdB;
                    else if (result.TeamId == req.IdB) result.TeamId = req.IdA;
                }
            }
        }

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> AdvanceToKnockoutAsync(int eventId, int programId)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (!string.Equals(state.Phase, "group", StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("WRONG_PHASE", "Already in knockout phase.");

        if (!state.Groups.All(g => g.Matches.All(IsCompleted)))
            return FixtureGenerationResult.Fail("GROUP_NOT_DONE", "Complete all group matches before generating the knockout phase.");

        state.Phase = "knockout";
        state.Matches = GenerateKnockoutFromGroups(state.Groups, state.Config);
        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> AdvanceKnockoutRoundAsync(int eventId, int programId)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (!string.Equals(state.Phase, "knockout", StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("WRONG_PHASE", "This fixture is not in knockout phase.");
        if (!state.Matches.Any())
            return FixtureGenerationResult.Fail("NOT_FOUND", "No knockout matches found.");

        var maxRound = state.Matches.Max(m => m.Round);
        var currentRound = state.Matches.Where(m => m.Round == maxRound).ToList();
        if (currentRound.Count <= 1)
            return FixtureGenerationResult.Fail("FINAL_ROUND", "No further knockout rounds remain.");
        if (currentRound.Any(m => !IsCompleted(m)))
            return FixtureGenerationResult.Fail("ROUND_NOT_DONE", "Complete the current knockout round before advancing.");

        state.Matches.AddRange(GenerateNextKnockoutRound(state.Matches));
        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> SaveScoreAsync(int eventId, int programId, string matchId, SaveFixtureScoreRequest req)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        var match = FindMatch(state, matchId);
        if (match == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Match not found.");

        match.Games = req.Games.Select(g => new GameScore { P1 = g.P1, P2 = g.P2 }).ToList();
        if (!match.Games.Any()) match.Games = new List<GameScore> { new() };
        match.Walkover = req.Walkover;
        match.WalkoverWinner = req.Walkover ? req.WalkoverWinner : "";
        match.Winner = req.Walkover ? req.WalkoverWinner : req.Winner;
        match.Officials = req.Officials.Select(o => new OfficialEntry { Id = o.Id, Role = o.Role, Name = o.Name }).ToList();
        match.Status = req.Walkover ? "Walkover" : "Completed";
        state.Locked = true;

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> UpdateScheduleAsync(int eventId, int programId, string matchId, UpdateFixtureScheduleRequest req)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        var match = FindMatch(state, matchId);
        if (match == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Match not found.");

        match.CourtNo = req.CourtNo ?? "";
        match.MatchDate = req.MatchDate ?? "";
        match.StartTime = req.StartTime ?? "";
        match.EndTime = req.EndTime ?? "";

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> SaveHeatResultAsync(int eventId, int programId, SaveHeatResultRequest req)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (!string.Equals(state.Format, "heats", StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("WRONG_FORMAT", "This fixture is not using heats.");

        var round = (state.HeatRounds ?? new List<HeatRound>()).FirstOrDefault(r => r.RoundNumber == req.RoundNumber);
        if (round == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Heat round not found.");

        var result = round.Results.FirstOrDefault(r => r.TeamId == req.TeamId);
        if (result == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Participant result not found.");

        result.Result = req.Result ?? "";

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> AdvanceHeatsRoundAsync(int eventId, int programId, AdvanceHeatsRoundRequest req)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (!string.Equals(state.Format, "heats", StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("WRONG_FORMAT", "This fixture is not using heats.");

        var rounds = state.HeatRounds ?? new List<HeatRound>();
        var round = rounds.FirstOrDefault(r => r.RoundNumber == req.FromRound);
        if (round == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Heat round not found.");
        if (round.IsComplete)
            return FixtureGenerationResult.Fail("ALREADY_COMPLETE", "This round has already been advanced.");

        var nextRound = rounds.FirstOrDefault(r => r.RoundNumber == req.FromRound + 1);
        if (nextRound == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Next heat round not found.");

        var advancing = new HashSet<string>(req.AdvancingIds ?? new List<string>(), StringComparer.Ordinal);
        if (advancing.Count == 0)
            return FixtureGenerationResult.Fail("INVALID_ADVANCE", "Select at least one participant to advance.");
        if (advancing.Any(id => round.Results.All(r => r.TeamId != id)))
            return FixtureGenerationResult.Fail("INVALID_ADVANCE", "One or more advancing participants are invalid.");

        round.IsComplete = true;
        foreach (var item in round.Results)
            item.Advanced = advancing.Contains(item.TeamId);

        nextRound.Results = round.Results
            .Where(r => advancing.Contains(r.TeamId))
            .Select(r => new HeatParticipantResult { TeamId = r.TeamId, Result = "", Advanced = false })
            .ToList();

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    public async Task<FixtureGenerationResult> AssignHeatPlacesAsync(int eventId, int programId, AssignHeatPlacesRequest req)
    {
        var loaded = await LoadExistingStateAsync(eventId, programId);
        if (!loaded.Success) return loaded;
        var state = loaded.State!;

        if (!string.Equals(state.Format, "heats", StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("WRONG_FORMAT", "This fixture is not using heats.");

        var finalRound = (state.HeatRounds ?? new List<HeatRound>()).FirstOrDefault(r => r.IsFinal);
        if (finalRound == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Final heat round not found.");

        var places = req.Places ?? new Dictionary<string, int>();
        foreach (var result in finalRound.Results)
        {
            if (places.TryGetValue(result.TeamId, out var place))
            {
                result.Place = place;
                result.Advanced = true;
            }
        }
        finalRound.IsComplete = true;

        await SaveStateAsync(loaded.Fixture!, state);
        return FixtureGenerationResult.Ok(state);
    }

    private FixtureGenerationResult NormalizeSeeds(List<ParticipantGroup> groups, List<FixtureSeedEntryRequest> requested)
    {
        var requestedById = requested.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var actualIds = groups.Select(g => g.GroupId).OrderBy(x => x).ToList();
        var requestedIds = requestedById.Keys
            .Select(id => int.TryParse(id, out var parsed) ? parsed : -1)
            .OrderBy(x => x)
            .ToList();
        if (!actualIds.SequenceEqual(requestedIds))
            return FixtureGenerationResult.Fail("PARTICIPANTS_CHANGED", "Registered entries changed. Reload the page and try again.");

        var seeds = groups.Select(g =>
        {
            var req = requestedById[g.GroupId.ToString()];
            return new FixtureSeedEntry
            {
                Id = g.GroupId.ToString(),
                GroupId = g.GroupId.ToString(),
                RegistrationId = g.RegistrationId.ToString(),
                Club = g.ClubDisplay ?? "",
                Participants = g.Participants.Select(p => p.FullName).ToList(),
                Seed = req.Seed,
                SbaId = g.Participants.FirstOrDefault()?.SbaId,
            };
        }).ToList();

        var seedNums = seeds.Where(s => s.Seed.HasValue).Select(s => s.Seed!.Value).ToList();
        if (seedNums.Any(n => n < 1))
            return FixtureGenerationResult.Fail("INVALID_SEED", "Seed numbers must be positive.");
        if (seedNums.Count != seedNums.Distinct().Count())
            return FixtureGenerationResult.Fail("DUPLICATE_SEEDS", "Duplicate seed numbers are not allowed.");

        return FixtureGenerationResult.Ok(new FixtureState { Seeds = seeds });
    }

    private FixtureGenerationResult NormalizeConfig(FixtureConfigRequest req)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "knockout", "group_knockout", "round_robin", "heats"
        };
        if (!allowed.Contains(req.Format))
            return FixtureGenerationResult.Fail("INVALID_FORMAT", "Unsupported fixture format.");

        if (req.NumSeeds < 0)
            return FixtureGenerationResult.Fail("INVALID_CONFIG", "Number of seeds cannot be negative.");

        var config = new FixtureConfig
        {
            Format = req.Format,
            NumSeeds = req.NumSeeds,
            NumGroups = req.NumGroups,
            AdvancePerGroup = req.AdvancePerGroup,
            StandingPoints = req.StandingPoints == null ? null : new StandingPoints
            {
                Win = req.StandingPoints.Win,
                Draw = req.StandingPoints.Draw,
                Loss = req.StandingPoints.Loss,
            },
            HeatsConfig = req.HeatsConfig == null ? null : new HeatsConfig
            {
                NumRounds = req.HeatsConfig.NumRounds,
                AdvancePerRound = req.HeatsConfig.AdvancePerRound,
                ResultLabel = string.IsNullOrWhiteSpace(req.HeatsConfig.ResultLabel) ? "Result" : req.HeatsConfig.ResultLabel,
                PlacesAwarded = req.HeatsConfig.PlacesAwarded,
            },
        };

        return FixtureGenerationResult.Ok(new FixtureState { Config = config });
    }

    private FixtureGenerationResult ValidatePreview(string json, List<FixtureSeedEntry> seeds, FixtureConfig config)
    {
        FixtureState? state;
        try
        {
            state = JsonSerializer.Deserialize<FixtureState>(json, _jsonOptions);
        }
        catch
        {
            return FixtureGenerationResult.Fail("INVALID_PREVIEW", "Preview fixture data is invalid.");
        }

        if (state == null)
            return FixtureGenerationResult.Fail("INVALID_PREVIEW", "Preview fixture data is invalid.");

        if (!string.Equals(state.Format, config.Format, StringComparison.OrdinalIgnoreCase))
            return FixtureGenerationResult.Fail("FORMAT_MISMATCH", "Preview fixture format does not match the selected format.");

        state.Config = config;
        state.Seeds = seeds;
        state.Locked = false;

        var allowedIds = new HashSet<string>(seeds.Select(s => s.Id), StringComparer.Ordinal);
        bool IsAllowedTeamId(string id) => allowedIds.Contains(id) || id.StartsWith("bye-", StringComparison.Ordinal);

        foreach (var group in state.Groups)
        {
            foreach (var team in group.Teams)
            {
                if (!IsAllowedTeamId(team.Id))
                    return FixtureGenerationResult.Fail("INVALID_TEAM", "Preview references an unknown team.");
            }

            foreach (var match in group.Matches)
            {
                if (!IsAllowedTeamId(match.Team1.Id) || !IsAllowedTeamId(match.Team2.Id))
                    return FixtureGenerationResult.Fail("INVALID_TEAM", "Preview references an unknown team.");
            }
        }

        foreach (var match in state.Matches)
        {
            if (!IsAllowedTeamId(match.Team1.Id) || !IsAllowedTeamId(match.Team2.Id))
                return FixtureGenerationResult.Fail("INVALID_TEAM", "Preview references an unknown team.");
        }

        foreach (var round in state.HeatRounds ?? new List<HeatRound>())
        {
            if (round.Results.Any(r => !allowedIds.Contains(r.TeamId)))
                return FixtureGenerationResult.Fail("INVALID_TEAM", "Preview references an unknown team.");
        }

        return FixtureGenerationResult.Ok(state, json);
    }

    private async Task<FixtureGenerationResult> LoadExistingStateAsync(int eventId, int programId)
    {
        var fixture = await _db.Fixtures.FirstOrDefaultAsync(f => f.EventId == eventId && f.ProgramId == programId);
        if (fixture == null)
            return FixtureGenerationResult.Fail("NOT_FOUND", "Fixture not found.");

        FixtureState? state;
        try
        {
            state = JsonSerializer.Deserialize<FixtureState>(fixture.BracketStateJson, _jsonOptions);
        }
        catch
        {
            return FixtureGenerationResult.Fail("PARSE_FAILED", "Fixture data is corrupted.");
        }

        if (state == null)
            return FixtureGenerationResult.Fail("PARSE_FAILED", "Fixture data is corrupted.");

        return new FixtureGenerationResult
        {
            Success = true,
            State = state,
            StateJson = fixture.BracketStateJson,
            Fixture = fixture,
        };
    }

    private async Task SaveStateAsync(Fixture fixture, FixtureState state)
    {
        fixture.BracketStateJson = JsonSerializer.Serialize(state, _jsonOptions);
        fixture.FixtureFormat = state.Format;
        fixture.Phase = state.Phase;
        fixture.IsLocked = state.Locked;
        fixture.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private bool IsLocked(FixtureState state)
    {
        if (state.Format == "heats")
            return (state.HeatRounds ?? new List<HeatRound>()).Any(r => r.IsComplete);

        return state.Matches.Concat(state.Groups.SelectMany(g => g.Matches))
            .Any(m => !string.Equals(m.Status, "Scheduled", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCompleted(FixtureMatch match) =>
        string.Equals(match.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(match.Status, "Walkover", StringComparison.OrdinalIgnoreCase);

    private FixtureMatch? FindMatch(FixtureState state, string matchId)
    {
        foreach (var group in state.Groups)
        {
            var match = group.Matches.FirstOrDefault(m => m.Id == matchId);
            if (match != null) return match;
        }

        return state.Matches.FirstOrDefault(m => m.Id == matchId);
    }

    private List<FixtureMatch> GenerateKnockoutFromGroups(List<FixtureGroup> groups, FixtureConfig config)
    {
        var advance = config.AdvancePerGroup ?? 2;
        var advancers = groups
            .Select(g => ComputeGroupStandings(g, config).Take(advance).Select(s => s.Team).ToList())
            .ToList();

        var paired = new List<(FixtureTeam Team1, FixtureTeam Team2)>();
        if (groups.Count == 2)
        {
            var groupA = advancers.ElementAtOrDefault(0) ?? new List<FixtureTeam>();
            var groupB = advancers.ElementAtOrDefault(1) ?? new List<FixtureTeam>();
            for (var i = 0; i < advance; i++)
            {
                var t1 = groupA.ElementAtOrDefault(i);
                var t2 = groupB.ElementAtOrDefault(advance - 1 - i);
                if (t1 != null && t2 != null) paired.Add((t1, t2));
            }
        }
        else
        {
            for (var i = 0; i < advance; i++)
            {
                for (var j = 0; j < groups.Count - 1; j++)
                {
                    var t1 = advancers.ElementAtOrDefault(j)?.ElementAtOrDefault(i);
                    var t2 = advancers.ElementAtOrDefault(j + 1)?.ElementAtOrDefault(i);
                    if (t1 != null && t2 != null) paired.Add((t1, t2));
                }
            }
        }

        var matches = paired.Select(p => BlankMatch(p.Team1, p.Team2, 1, "knockout")).ToList();
        ApplyRoundLabels(matches);
        return matches;
    }

    private List<FixtureMatch> GenerateNextKnockoutRound(List<FixtureMatch> matches)
    {
        var maxRound = matches.Max(m => m.Round);
        var currentRound = matches.Where(m => m.Round == maxRound).ToList();
        var winners = currentRound.Select(m =>
            m.Winner == "team1" ? m.Team1 :
            m.Winner == "team2" ? m.Team2 :
            m.Team1).ToList();

        var nextRound = new List<FixtureMatch>();
        for (var i = 0; i < winners.Count - 1; i += 2)
            nextRound.Add(BlankMatch(winners[i], winners[i + 1], maxRound + 1, "knockout"));

        var all = matches.Concat(nextRound).ToList();
        ApplyRoundLabels(all);
        return all.Where(m => m.Round > maxRound).ToList();
    }

    private List<GroupStandingEntry> ComputeGroupStandings(FixtureGroup group, FixtureConfig config)
    {
        var standings = group.Teams.ToDictionary(
            t => t.Id,
            t => new GroupStandingEntry { Team = t },
            StringComparer.Ordinal);

        var scoring = config.StandingPoints ?? new StandingPoints { Win = 2, Draw = 1, Loss = 0 };
        var headToHead = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var match in group.Matches.Where(IsCompleted))
        {
            if (!standings.TryGetValue(match.Team1.Id, out var s1) || !standings.TryGetValue(match.Team2.Id, out var s2))
                continue;

            s1.Played++;
            s2.Played++;

            foreach (var game in match.Games)
            {
                if (!decimal.TryParse(game.P1, out var p1) || !decimal.TryParse(game.P2, out var p2))
                    continue;

                s1.PointsFor += p1;
                s1.PointsAgainst += p2;
                s2.PointsFor += p2;
                s2.PointsAgainst += p1;

                if (p1 > p2)
                {
                    s1.GamesFor++;
                    s2.GamesAgainst++;
                }
                else if (p2 > p1)
                {
                    s2.GamesFor++;
                    s1.GamesAgainst++;
                }
            }

            if (match.Winner == "team1")
            {
                s1.Wins++;
                s1.Points += scoring.Win;
                s2.Losses++;
                s2.Points += scoring.Loss;
                RecordHeadToHead(headToHead, match.Team1.Id, match.Team2.Id, -1);
            }
            else if (match.Winner == "team2")
            {
                s2.Wins++;
                s2.Points += scoring.Win;
                s1.Losses++;
                s1.Points += scoring.Loss;
                RecordHeadToHead(headToHead, match.Team1.Id, match.Team2.Id, 1);
            }
            else
            {
                s1.Draws++;
                s2.Draws++;
                s1.Points += scoring.Draw;
                s2.Points += scoring.Draw;
                RecordHeadToHead(headToHead, match.Team1.Id, match.Team2.Id, 0);
            }
        }

        var ordered = standings.Values.ToList();
        ordered.Sort((a, b) =>
        {
            var pointsDiff = b.Points.CompareTo(a.Points);
            if (pointsDiff != 0) return pointsDiff;

            var h2h = CompareHeadToHead(a, b, headToHead);
            if (h2h != 0) return h2h;

            var gameRatio = (b.GamesFor / (decimal)Math.Max(b.Played, 1))
                .CompareTo(a.GamesFor / (decimal)Math.Max(a.Played, 1));
            if (gameRatio != 0) return gameRatio;

            var pointRatio = (b.PointsFor / (decimal)(b.PointsAgainst == 0 ? 1 : b.PointsAgainst))
                .CompareTo(a.PointsFor / (decimal)(a.PointsAgainst == 0 ? 1 : a.PointsAgainst));
            if (pointRatio != 0) return pointRatio;

            return string.Compare(a.Team.Id, b.Team.Id, StringComparison.Ordinal);
        });

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Rank = i + 1;

        return ordered;
    }

    private void RecordHeadToHead(Dictionary<string, Dictionary<string, int>> headToHead, string teamA, string teamB, int compareResult)
    {
        if (!headToHead.TryGetValue(teamA, out var mapA))
        {
            mapA = new Dictionary<string, int>(StringComparer.Ordinal);
            headToHead[teamA] = mapA;
        }
        if (!headToHead.TryGetValue(teamB, out var mapB))
        {
            mapB = new Dictionary<string, int>(StringComparer.Ordinal);
            headToHead[teamB] = mapB;
        }

        mapA[teamB] = compareResult;
        mapB[teamA] = -compareResult;
    }

    private int CompareHeadToHead(GroupStandingEntry a, GroupStandingEntry b, Dictionary<string, Dictionary<string, int>> headToHead)
    {
        if (headToHead.TryGetValue(a.Team.Id, out var opponents) &&
            opponents.TryGetValue(b.Team.Id, out var result))
            return result;

        return 0;
    }

    private FixtureState GenerateState(List<FixtureSeedEntry> seeds, FixtureConfig config)
    {
        if (config.Format == "heats")
            return GenerateHeatsState(seeds, config);

        return config.Format switch
        {
            "knockout" => new FixtureState
            {
                Format = config.Format,
                Config = config,
                Locked = false,
                Phase = "knockout",
                Seeds = seeds,
                Matches = GenerateKnockoutMatches(ToTeams(SortedSeeds(seeds))),
            },
            "group_knockout" => new FixtureState
            {
                Format = config.Format,
                Config = config,
                Locked = false,
                Phase = "group",
                Seeds = seeds,
                Groups = GenerateGroupDraw(seeds, config.NumGroups ?? 2),
            },
            "round_robin" => new FixtureState
            {
                Format = config.Format,
                Config = config,
                Locked = false,
                Phase = "group",
                Seeds = seeds,
                Groups = GenerateGroupDraw(seeds, 1),
            },
            _ => new FixtureState
            {
                Format = config.Format,
                Config = config,
                Locked = false,
                Phase = "knockout",
                Seeds = seeds,
            },
        };
    }

    private FixtureState GenerateHeatsState(List<FixtureSeedEntry> seeds, FixtureConfig config)
    {
        var hc = config.HeatsConfig ?? new HeatsConfig
        {
            NumRounds = 2,
            AdvancePerRound = 4,
            ResultLabel = "Result",
            PlacesAwarded = 3,
        };

        var heatRounds = Enumerable.Range(1, hc.NumRounds).Select(i =>
        {
            var isFirst = i == 1;
            var isFinal = i == hc.NumRounds;
            var label = isFinal ? "Final" : hc.NumRounds == 2 ? "Heat" : i == 1 ? "Heat" : $"Round {i}";
            return new HeatRound
            {
                Id = $"HR-{i}",
                RoundNumber = i,
                Label = label,
                IsFinal = isFinal,
                IsComplete = false,
                Results = isFirst
                    ? seeds.Select(s => new HeatParticipantResult { TeamId = s.Id, Result = "", Advanced = false }).ToList()
                    : new List<HeatParticipantResult>(),
            };
        }).ToList();

        return new FixtureState
        {
            Format = "heats",
            Config = config,
            Locked = false,
            Phase = "knockout",
            Seeds = seeds,
            HeatRounds = heatRounds,
        };
    }

    private List<FixtureGroup> GenerateGroupDraw(List<FixtureSeedEntry> seeds, int numGroups)
    {
        var sorted = SortedSeeds(seeds);
        var groups = Enumerable.Range(0, numGroups).Select(i => new FixtureGroup
        {
            Id = $"G{i + 1}",
            Name = $"Group {(char)('A' + i)}",
        }).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var groupIndex = i % (numGroups * 2) < numGroups ? i % numGroups : numGroups - 1 - (i % numGroups);
            groups[groupIndex].Teams.Add(ToTeam(sorted[i]));
        }

        foreach (var group in groups)
        {
            for (var a = 0; a < group.Teams.Count - 1; a++)
            {
                for (var b = a + 1; b < group.Teams.Count; b++)
                {
                    group.Matches.Add(BlankMatch(group.Teams[a], group.Teams[b], 1, "group", group.Id));
                }
            }
        }

        return groups;
    }

    private List<FixtureMatch> GenerateKnockoutMatches(List<FixtureTeam> teams)
    {
        var pow = 1;
        while (pow < teams.Count) pow *= 2;

        var slots = Enumerable.Repeat<FixtureTeam?>(null, pow).ToList();
        var seedOrder = new[] { 0, pow - 1, pow / 2 - 1, pow / 2 };
        for (var i = 0; i < teams.Count; i++)
        {
            var pos = i < seedOrder.Length ? seedOrder[i] : -1;
            if (pos != -1 && slots[pos] == null) slots[pos] = teams[i];
            else
            {
                var empty = slots.FindIndex(s => s == null);
                slots[empty] = teams[i];
            }
        }

        var matches = new List<FixtureMatch>();
        for (var i = 0; i < pow; i += 2)
        {
            matches.Add(BlankMatch(slots[i] ?? ByeTeam(), slots[i + 1] ?? ByeTeam(), 1, "knockout"));
        }

        ApplyRoundLabels(matches);
        return matches;
    }

    private List<FixtureSeedEntry> SortedSeeds(List<FixtureSeedEntry> seeds)
    {
        var seeded = seeds.Where(s => s.Seed.HasValue).OrderBy(s => s.Seed).ToList();
        var unseeded = seeds.Where(s => !s.Seed.HasValue).OrderBy(_ => Random.Shared.Next()).ToList();
        return seeded.Concat(unseeded).ToList();
    }

    private List<FixtureTeam> ToTeams(List<FixtureSeedEntry> seeds) => seeds.Select(ToTeam).ToList();

    private FixtureTeam ToTeam(FixtureSeedEntry seed) => new()
    {
        Id = seed.Id,
        Label = seed.Club,
        Participants = seed.Participants,
        Seed = seed.Seed,
    };

    private FixtureMatch BlankMatch(FixtureTeam team1, FixtureTeam team2, int round, string phase, string? groupId = null) => new()
    {
        Id = $"M-{Guid.NewGuid():N}",
        Phase = phase,
        Round = round,
        RoundLabel = "",
        GroupId = groupId,
        Team1 = team1,
        Team2 = team2,
        Games = new List<GameScore> { new() },
        Winner = null,
        Walkover = false,
        WalkoverWinner = "",
        MatchDate = "",
        StartTime = "",
        EndTime = "",
        CourtNo = "",
        Officials = new List<OfficialEntry>(),
        Status = "Scheduled",
        Expanded = false,
    };

    private FixtureTeam ByeTeam() => new()
    {
        Id = $"bye-{Guid.NewGuid():N}",
        Label = "BYE",
        Participants = new List<string>(),
    };

    private void ApplyRoundLabels(List<FixtureMatch> matches)
    {
        var rounds = matches.Select(m => m.Round).Distinct().OrderBy(r => r).ToList();
        var lastRound = rounds.LastOrDefault();
        foreach (var match in matches.Where(m => m.Phase == "knockout"))
        {
            var inRound = matches.Count(m => m.Round == match.Round && m.Phase == "knockout");
            match.RoundLabel = match.Round == lastRound && inRound == 1
                ? "Final"
                : inRound switch
                {
                    1 => "Final",
                    2 => "Semi-Final",
                    4 => "Quarter-Final",
                    _ => $"Round of {inRound * 2}",
                };
        }
    }

    public sealed class FixtureGenerationResult
    {
        public bool Success { get; init; }
        public string? Code { get; init; }
        public string Message { get; init; } = "";
        public FixtureState? State { get; init; }
        public string? StateJson { get; init; }
        public Fixture? Fixture { get; init; }

        public static FixtureGenerationResult Ok(FixtureState state, string? json = null) => new()
        {
            Success = true,
            State = state,
            StateJson = json,
        };

        public static FixtureGenerationResult Fail(string code, string message) => new()
        {
            Success = false,
            Code = code,
            Message = message,
        };
    }

    public sealed class FixtureState
    {
        public string Format { get; set; } = "knockout";
        public FixtureConfig Config { get; set; } = new();
        public bool Locked { get; set; }
        public string Phase { get; set; } = "knockout";
        public List<FixtureGroup> Groups { get; set; } = new();
        public List<FixtureMatch> Matches { get; set; } = new();
        public List<FixtureSeedEntry> Seeds { get; set; } = new();
        public List<HeatRound>? HeatRounds { get; set; }
    }

    public sealed class FixtureConfig
    {
        public string Format { get; set; } = "knockout";
        public int NumSeeds { get; set; }
        public int? NumGroups { get; set; }
        public int? AdvancePerGroup { get; set; }
        public StandingPoints? StandingPoints { get; set; }
        public HeatsConfig? HeatsConfig { get; set; }
    }

    public sealed class StandingPoints
    {
        public int Win { get; set; }
        public int Draw { get; set; }
        public int Loss { get; set; }
    }

    public sealed class HeatsConfig
    {
        public int NumRounds { get; set; }
        public int AdvancePerRound { get; set; }
        public string ResultLabel { get; set; } = "Result";
        public int PlacesAwarded { get; set; }
    }

    public sealed class FixtureSeedEntry
    {
        public string Id { get; set; } = "";
        public string Club { get; set; } = "";
        public List<string> Participants { get; set; } = new();
        public int? Seed { get; set; }
        public string? SbaId { get; set; }
        public string? RegistrationId { get; set; }
        public string? GroupId { get; set; }
    }

    public sealed class FixtureTeam
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public List<string> Participants { get; set; } = new();
        public int? Seed { get; set; }
    }

    public sealed class FixtureGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<FixtureTeam> Teams { get; set; } = new();
        public List<FixtureMatch> Matches { get; set; } = new();
    }

    public sealed class FixtureMatch
    {
        public string Id { get; set; } = "";
        public string Phase { get; set; } = "group";
        public int Round { get; set; }
        public string RoundLabel { get; set; } = "";
        public string? GroupId { get; set; }
        public FixtureTeam Team1 { get; set; } = new();
        public FixtureTeam Team2 { get; set; } = new();
        public List<GameScore> Games { get; set; } = new();
        public string? Winner { get; set; }
        public bool Walkover { get; set; }
        public string WalkoverWinner { get; set; } = "";
        public string MatchDate { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public string CourtNo { get; set; } = "";
        public List<OfficialEntry> Officials { get; set; } = new();
        public string Status { get; set; } = "Scheduled";
        public bool Expanded { get; set; }
    }

    public sealed class GameScore
    {
        public string P1 { get; set; } = "";
        public string P2 { get; set; } = "";
    }

    public sealed class OfficialEntry
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class HeatRound
    {
        public string Id { get; set; } = "";
        public int RoundNumber { get; set; }
        public string Label { get; set; } = "";
        public bool IsFinal { get; set; }
        public List<HeatParticipantResult> Results { get; set; } = new();
        public bool IsComplete { get; set; }
    }

    public sealed class HeatParticipantResult
    {
        public string TeamId { get; set; } = "";
        public string Result { get; set; } = "";
        public bool Advanced { get; set; }
        public int? Place { get; set; }
    }

    private sealed class GroupStandingEntry
    {
        public FixtureTeam Team { get; set; } = new();
        public int Played { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Draws { get; set; }
        public int GamesFor { get; set; }
        public int GamesAgainst { get; set; }
        public decimal PointsFor { get; set; }
        public decimal PointsAgainst { get; set; }
        public int Points { get; set; }
        public int Rank { get; set; }
    }
}
