using Microsoft.AspNetCore.Builder;
using Nexus.Service.Auth;
using Nexus.Service.Games;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// Local fps session surface: the local-data tracking switch and purge
/// (mirroring /api/screentime), and read-only game/session summaries served
/// from BinaryFpsSessionStore. Pinned cross-repo contract with nexus-web -
/// field names and shapes must not change on one side alone.
/// </summary>
public static class FpsRoutes
{
    private const int DefaultSessionsLimit = 50;
    private const int MinSessionsLimit = 1;
    private const int MaxSessionsLimit = 200;

    public static void MapFpsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/fps/tracking", (IConfigStore config) =>
            new TrackingStatus { Enabled = config.Load().Fps?.TrackingEnabled ?? true });

        app.MapPost("/api/fps/tracking", (SetTrackingBody body, IConfigStore config) =>
        {
            config.Update(s =>
            {
                s.Fps ??= new FpsSettings();
                s.Fps.TrackingEnabled = body.Enabled;
            });
            return new TrackingStatus { Enabled = body.Enabled };
        });

        app.MapDelete("/api/fps/all", (BinaryFpsSessionStore store, IMetricsHistoryStore history) =>
        {
            var deleted = store.DeleteAll();
            history.BlankFpsSeries();
            return new DeleteResponse { Deleted = deleted };
        });

        app.MapGet("/api/fps/games", (BinaryFpsSessionStore store) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return new FpsGamesResponse { Supported = false };
            }

            var games = store.QueryGameSummaries()
                .Select(ToGameDto)
                .OrderByDescending(g => g.LastPlayedUtcMs)
                .ToList();
            return new FpsGamesResponse { Supported = true, Games = games };
        }).AllowPanel();

        app.MapGet("/api/fps/games/{gameKey}/sessions", (string gameKey, int? limit, BinaryFpsSessionStore store) =>
        {
            var clampedLimit = Math.Clamp(limit ?? DefaultSessionsLimit, MinSessionsLimit, MaxSessionsLimit);
            var sessions = store.QuerySessions(Uri.UnescapeDataString(gameKey), clampedLimit)
                .Select(ToSessionDto)
                .ToList();
            return new FpsSessionsResponse { Sessions = sessions };
        }).AllowPanel();
    }

    internal static FpsGameDto ToGameDto(FpsGameSummary s) => new()
    {
        GameKey = s.GameKey,
        Name = s.GameName,
        Store = s.Store,
        SteamAppId = int.TryParse(s.SteamAppId, out var appId) ? appId : null,
        Sessions = s.Sessions,
        FocusedSec = s.FocusedSec,
        AvgFps = AverageFps(s.Frames, s.ValidSec),
        P1Fps = FpsHistogram.Percentile(s.Hist, 1),
        P99Fps = FpsHistogram.Percentile(s.Hist, 99),
        MinFps = s.MinFps,
        MaxFps = s.MaxFps,
        LastPlayedUtcMs = s.LastPlayedUtcMs,
    };

    internal static FpsSessionDto ToSessionDto(FpsSessionRecord r) => new()
    {
        Id = r.Id.ToString(),
        StartedUtcMs = r.StartedUtcMs,
        EndedUtcMs = r.EndedUtcMs,
        FocusedSec = r.FocusedSec,
        ValidSec = r.ValidSec,
        AvgFps = AverageFps(r.Frames, r.ValidSec),
        P1Fps = FpsHistogram.Percentile(r.Hist, 1),
        P99Fps = FpsHistogram.Percentile(r.Hist, 99),
        MinFps = r.MinFps,
        MaxFps = r.MaxFps,
        DispW = r.DispW,
        DispH = r.DispH,
        RefreshHz = r.RefreshHz,
        Fullscreen = r.Fullscreen,
        Capped = r.Capped,
        CapValue = r.CapValue,
    };

    // Pure and directly unit-tested: sum-of-frames / sum-of-validSec is the
    // wire contract's avgFps, per the fps-benchmarks plan (not an average of
    // per-second values, which would over-weight seconds that report a
    // frame count of exactly 1).
    internal static double AverageFps(long frames, long validSec) =>
        validSec > 0 ? Math.Round((double)frames / validSec, 1) : 0;
}

public sealed record FpsGameDto
{
    public string GameKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Store { get; init; } = "";
    public int? SteamAppId { get; init; }
    public int Sessions { get; init; }
    public long FocusedSec { get; init; }
    public double AvgFps { get; init; }
    public int P1Fps { get; init; }
    public int P99Fps { get; init; }
    public int MinFps { get; init; }
    public int MaxFps { get; init; }
    public long LastPlayedUtcMs { get; init; }
}

public sealed record FpsGamesResponse
{
    public bool Supported { get; init; } = true;
    public IReadOnlyList<FpsGameDto> Games { get; init; } = Array.Empty<FpsGameDto>();
}

public sealed record FpsSessionDto
{
    public string Id { get; init; } = "";
    public long StartedUtcMs { get; init; }
    public long EndedUtcMs { get; init; }
    public int FocusedSec { get; init; }
    public int ValidSec { get; init; }
    public double AvgFps { get; init; }
    public int P1Fps { get; init; }
    public int P99Fps { get; init; }
    public int MinFps { get; init; }
    public int MaxFps { get; init; }
    public int DispW { get; init; }
    public int DispH { get; init; }
    public int RefreshHz { get; init; }
    public bool Fullscreen { get; init; }
    public bool Capped { get; init; }
    public int CapValue { get; init; }
}

public sealed record FpsSessionsResponse
{
    public IReadOnlyList<FpsSessionDto> Sessions { get; init; } = Array.Empty<FpsSessionDto>();
}
