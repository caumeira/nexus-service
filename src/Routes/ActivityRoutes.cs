using System;
using Qos.Service.Activity;
using Qos.Service.Activity.Storage;
using Qos.Service.Auth;
using Qos.Service.Models;
using Qos.Service.Models.Activity;
using Qos.Service.Persistence;

namespace Qos.Service.Routes;

public static class ActivityRoutes
{
    public static void MapActivityEndpoints(this WebApplication app)
    {
        // Screen time
        app.MapGet("/api/screentime", (IScreenTimeProvider st) =>
        {
            var session = st.GetCurrentSession();
            return session is not null ? Results.Ok(session) : Results.Ok(new FocusSession());
        });
        app.MapGet("/api/screentime/history", (IScreenTimeProvider st) =>
            Results.Ok(st.GetTodayUsage()));

        // Persistent history browsing
        app.MapGet("/api/screentime/day/{date}", (string date, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(store.GetDay(d));
        });

        app.MapGet("/api/screentime/range", (string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new List<DayTotal>(store.GetRange(f, t)));
        });

        app.MapGet("/api/screentime/app/{name}", (string name, string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(store.GetAppHistory(Uri.UnescapeDataString(name), f, t));
        });

        app.MapGet("/api/screentime/day/{date}/hour/{hour:int}", (string date, int hour, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new List<AppUsage>(store.GetHourUsage(d, hour)));
        });

        app.MapDelete("/api/screentime/day/{date}", (string date, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteDay(d) });
        });

        app.MapDelete("/api/screentime/range", (string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteRange(f, t) });
        });

        app.MapDelete("/api/screentime/app/{name}", (string name, IScreenTimeStore store) =>
            new DeleteResponse { Deleted = store.DeleteApp(Uri.UnescapeDataString(name)) });

        app.MapDelete("/api/screentime/all", (IScreenTimeStore store) =>
            new DeleteResponse { Deleted = store.DeleteAll() });

        app.MapGet("/api/screentime/tracking", (IConfigStore config) =>
            new TrackingStatus { Enabled = config.Load().ScreenTime?.TrackingEnabled ?? true });

        app.MapPost("/api/screentime/tracking", (SetTrackingBody body, IConfigStore config) =>
        {
            config.Update(s =>
            {
                s.ScreenTime ??= new ScreenTimeSettings();
                s.ScreenTime.TrackingEnabled = body.Enabled;
            });
            return new TrackingStatus { Enabled = body.Enabled };
        });

        // App detection
        app.MapPost("/api/appdetection/kill/{id}", (string id, IAppDetectionProvider ad) =>
            new KillAppResponse { Success = ad.Kill(id) });

        // Media
        app.MapGet("/api/media", (IMediaProvider m) => Results.Ok(m.GetSessions())).AllowPanel();
        app.MapPost("/api/media/{source}/control", (string source, MediaControlBody body, IMediaProvider m) =>
        {
            m.Control(source, body.Action);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapGet("/api/media/{source}/album-art", (string source, IMediaProvider m) =>
        {
            var bytes = m.GetAlbumArt(source);
            return bytes.Length == 0 ? Results.BadRequest() : Results.File(bytes, "image/png");
        }).AllowPanel();

        // Shortcuts
        app.MapGet("/shortcuts", (string? targetId, IShortcutsProvider s) =>
        {
            if (!string.IsNullOrEmpty(targetId))
            {
                var sc = s.GetById(targetId);
                return sc is null
                    ? Results.NotFound(new GetShortcutResponse { Error = true, Msg = "Shortcut not found" })
                    : Results.Ok(new GetShortcutResponse { Shortcut = sc });
            }
            return Results.Ok(new GetAllShortcutsResponse { Shortcuts = new(s.GetAll()) });
        }).AllowPanel();
        app.MapGet("/shortcuts/icon", (string? targetId, IShortcutsProvider s) =>
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return Results.BadRequest();
            }

            var bytes = s.GetIcon(targetId);
            return bytes.Length == 0 ? Results.NotFound() : Results.File(bytes, "image/png");
        }).AllowPanel();
        app.MapPost("/shortcuts/launch", (string? targetId, IShortcutsProvider s) =>
            s.Launch(targetId ?? "") ? ApiResponse.Ok() : ApiResponse.Fail("Not found")).AllowPanel();

        // Network
        app.MapGet("/api/network/top", (int? count, INetworkProvider n) =>
            new List<NetworkProcessInfo>(n.GetSnapshot().Take(count ?? 20)));
    }
}
