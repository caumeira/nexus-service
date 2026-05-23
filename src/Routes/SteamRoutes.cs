using Qos.Service.Auth;
using Qos.Service.Models.Steam;
using Qos.Service.Steam;

namespace Qos.Service.Routes;

public static class SteamRoutes
{
    public static void MapSteamEndpoints(this WebApplication app)
    {
        app.MapGet("/api/steam/config", (ISteamProvider steam) => steam.GetConfig()).AllowPanel();
        app.MapPost("/api/steam/config", (SteamConfigBody body, ISteamProvider steam) =>
        {
            steam.SetConfig(body);
            return steam.GetConfig();
        }).AllowPanel();
        app.MapGet("/api/steam/status", (ISteamProvider steam) => steam.GetStatus()).AllowPanel();
        app.MapGet("/api/steam/profile", async (ISteamProvider steam, CancellationToken ct) =>
            await steam.GetProfileAsync(ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/recent-games", async (ISteamProvider steam, CancellationToken ct) =>
            await steam.GetRecentGamesAsync(ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/owned-games", async (ISteamProvider steam, CancellationToken ct) =>
            await steam.GetOwnedGamesAsync(ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/friends", async (ISteamProvider steam, CancellationToken ct) =>
            await steam.GetFriendsAsync(ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/achievements/{appId:int}", async (int appId, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetAchievementsAsync(appId, ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/current-players/{appId:int}", async (int appId, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetCurrentPlayersAsync(appId, ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/news/{appId:int}", async (int appId, int? count, int? maxLength, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetNewsAsync(appId, count ?? 5, maxLength ?? 300, ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/global-achievements/{appId:int}", async (int appId, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetGlobalAchievementsAsync(appId, ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/user-stats/{appId:int}", async (int appId, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetUserStatsAsync(appId, ct).ConfigureAwait(false)).AllowPanel();
        app.MapGet("/api/steam/app-details/{appId:int}", async (int appId, ISteamProvider steam, CancellationToken ct) =>
            await steam.GetAppDetailsAsync(appId, ct).ConfigureAwait(false)).AllowPanel();
        app.MapPost("/api/steam/launch", (int? appId, ISteamProvider steam) => steam.Launch(appId)).AllowPanel();
    }
}
