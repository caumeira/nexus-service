using System;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Activity;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

public static class ActivityRoutes
{
    public static void MapActivityEndpoints(this WebApplication app)
    {
        // Screen time - persistent history browsing
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
        app.MapGet("/shortcuts/icon", (string? targetId, HttpContext ctx, IShortcutsProvider s) =>
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return Results.BadRequest();
            }

            var bytes = s.GetIcon(targetId);
            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // Content hash as the ETag: Results.File's entityTag param drives the
            // framework's own conditional-GET handling, so a matching
            // If-None-Match short-circuits to a bodyless 304 - the deck page and
            // WebView2 stop re-downloading icons that have not changed.
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var etag = new EntityTagHeaderValue($"\"{hash}\"");
            ctx.Response.Headers.CacheControl = "private, max-age=3600, must-revalidate";
            return Results.File(bytes, "image/png", entityTag: etag);
        }).AllowPanel();
        app.MapPost("/shortcuts/launch", (string? targetId, IShortcutsProvider s) =>
            s.Launch(targetId ?? "") ? ApiResponse.Ok() : ApiResponse.Fail("Not found")).AllowPanel();

    }
}
