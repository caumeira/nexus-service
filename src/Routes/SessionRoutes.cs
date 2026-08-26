using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;

namespace Nexus.Service.Routes;

/// <summary>
/// The dashboard route to restore when a window reopens. Deliberately process
/// memory and nothing else: the value must survive a window close but not a
/// service start, so localStorage (survives both) and the SPA itself (dies with
/// the window) are both the wrong side of that boundary. A cold start - system
/// boot or a proper service shutdown - therefore lands on home.
///
///   GET  /session/last-route  -> { path }
///   POST /session/last-route  -> { path }
/// </summary>
internal static class SessionRoutes
{
    // The SPA posts on every route change, so writes are far more frequent
    // than reads and neither side may block the other.
    private static string _lastRoute = "";

    /// <summary>Longer than any route the SPA composes; a longer body is a caller bug, not a route.</summary>
    private const int MaxRouteLength = 256;

    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/session/last-route", () =>
            Results.Ok(new LastRouteDto { Path = Volatile.Read(ref _lastRoute) })).LocalhostOnly();

        app.MapPost("/session/last-route", (LastRouteDto body) =>
        {
            Volatile.Write(ref _lastRoute, Sanitize(body?.Path));
            return Results.Ok(new LastRouteDto { Path = Volatile.Read(ref _lastRoute) });
        }).LocalhostOnly();
    }

    /// <summary>Keeps only an absolute same-origin path, so a stored value can never redirect a reopened window off-origin.</summary>
    internal static string Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }
        var value = path!.Trim();
        if (value.Length > MaxRouteLength
            || value[0] != '/'
            // "//host" and "/\host" are protocol-relative, not same-origin.
            || (value.Length > 1 && (value[1] == '/' || value[1] == '\\')))
        {
            return "";
        }
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return "";
            }
        }
        return value;
    }
}

public sealed class LastRouteDto
{
    public string Path { get; set; } = "";
}
