using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nexus.Service.Auth;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Routes;

/// <summary>
/// Widget marketplace endpoints. The runtime is declarative now (the host
/// renders the widget from its manifest's view tree) so there is no
/// per-widget iframe origin and no bundled HTML/JS to serve as a page.
/// What this exposes:
///
/// <list type="bullet">
/// <item><c>GET /widgets-api/installed</c> - everything the registry sees.</item>
/// <item><c>GET /widgets-api/installed/{id}</c> - one widget, including manifest view tree.</item>
/// <item><c>GET /widgets-api/installed/{id}/asset/{**path}</c> - static assets
///   (icons, SVGs) under the widget bundle. Restricted to image extensions
///   (PNG/JPG/WEBP/GIF/ICO/SVG); no manifest-tree binding enforced.</item>
/// <item><c>GET / PATCH /widgets-api/installed/{id}/settings</c> - per-widget user settings.</item>
/// <item><c>GET /widgets-api/code/{sessionId}/worker.js</c> + sibling module
///   files - the Tier 2 worker source, served behind a per-spawn session
///   token rather than a Bearer-authed installed-route. Only widgets that
///   declared <c>code: worker</c> can mint a session.</item>
/// </list>
///
/// Install, uninstall, available list, and fetch-proxy endpoints are
/// registered below alongside the asset / settings routes.
/// </summary>
public static class WidgetRoutes
{
    public const string ApiPrefix = "/widgets-api/";

    private static readonly HashSet<string> AllowedAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico",
        ".svg",
    };

    // Tier 2 widget worker source files. Restricted to JS modules and JSON
    // sidecar data; CSS/HTML/anything-else isn't useful inside a worker
    // and would just widen the exfil surface if a widget bundle were ever
    // crafted to ship typo'd files.
    private static readonly HashSet<string> AllowedCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".json",
    };

    public static void MapWidgetEndpoints(this WebApplication app)
    {
        app.MapGet("/widgets-api/installed", (WidgetRegistry registry) =>
        {
            var response = new WidgetInstalledListingResponse();
            foreach (var entry in registry.All())
            {
                response.Widgets.Add(BuildListing(entry));
            }
            return Results.Json(response, AppJsonContext.Default.WidgetInstalledListingResponse);
        }).AllowPanel();

        app.MapGet("/widgets-api/installed/{id}", (string id, WidgetRegistry registry) =>
        {
            if (!WidgetIds.IsValid(id)) return Results.NotFound();
            if (!registry.TryGet(id, out var entry)) return Results.NotFound();
            return Results.Json(BuildListing(entry), AppJsonContext.Default.WidgetInstalledListing);
        }).AllowPanel();

        app.MapGet("/widgets-api/instance/{instanceId}/settings",
            (string instanceId, WidgetSettingsService settings) =>
        {
            // Instance ids are GUIDs assigned at widget-placement time, so we
            // don't apply the marketplace-id charset whitelist. The service
            // returns an empty doc when the id doesn't resolve to a known
            // placement — the SPA renders that as "no settings".
            return Results.Json(settings.Get(instanceId), AppJsonContext.Default.WidgetSettingsDocument);
        }).AllowPanel();

        app.MapPatch("/widgets-api/instance/{instanceId}/settings",
            (string instanceId, WidgetSettingsPatch body, WidgetSettingsService settings) =>
        {
            var updated = settings.Apply(instanceId, body);
            return Results.Json(updated, AppJsonContext.Default.WidgetSettingsDocument);
        }).AllowPanel();

        app.MapGet("/widgets-api/installed/{id}/asset/{**path}",
            (string id, string? path, HttpContext ctx, WidgetRegistry registry) =>
            ServeAsset(id, path, ctx, registry, requireImage: true)).AllowPanel();

        app.MapGet("/widgets-api/available", (WidgetInstaller installer) =>
        {
            return Results.Json(installer.Catalogue(), AppJsonContext.Default.WidgetCatalogResponse);
        }).AllowPanel();

        app.MapPost("/widgets-api/install", (WidgetInstallRequest body, WidgetInstaller installer) =>
        {
            var result = installer.Install(body.Id ?? "");
            return Results.Json(result, AppJsonContext.Default.WidgetInstallResponse);
        }).AllowPanel();

        app.MapPost("/widgets-api/uninstall", (WidgetInstallRequest body, WidgetInstaller installer) =>
        {
            var result = installer.Uninstall(body.Id ?? "");
            return Results.Json(result, AppJsonContext.Default.WidgetInstallResponse);
        }).AllowPanel();

        // Host-action dispatch. Widgets POST { widgetId, action, args };
        // the action is validated against the manifest's capabilities.dispatch
        // allowlist and then routed to the registered handler. Returns
        // the handler's result (action-specific JsonElement shape).
        app.MapPost("/widgets-api/dispatch",
            async (WidgetDispatchRequest body, WidgetRegistry registry,
                   WidgetActionRegistry actions, IServiceProvider services,
                   HttpContext ctx) =>
        {
            if (!WidgetIds.IsValid(body.WidgetId))
                return Results.Json(new WidgetDispatchResponse { Ok = false, Error = "invalid widget id" },
                    AppJsonContext.Default.WidgetDispatchResponse, statusCode: 400);
            if (!registry.TryGet(body.WidgetId, out var entry))
                return Results.Json(new WidgetDispatchResponse { Ok = false, Error = "widget not installed" },
                    AppJsonContext.Default.WidgetDispatchResponse, statusCode: 404);
            if (!entry.Manifest.Capabilities.Dispatch.Contains(body.Action))
                return Results.Json(new WidgetDispatchResponse { Ok = false, Error = $"action '{body.Action}' not in manifest allowlist" },
                    AppJsonContext.Default.WidgetDispatchResponse, statusCode: 403);
            if (!actions.TryGet(body.Action, out var handler))
                return Results.Json(new WidgetDispatchResponse { Ok = false, Error = $"action '{body.Action}' is not registered" },
                    AppJsonContext.Default.WidgetDispatchResponse, statusCode: 404);

            try
            {
                var result = await handler(services, body.Args, ctx.RequestAborted);
                return Results.Json(new WidgetDispatchResponse { Ok = true, Result = result },
                    AppJsonContext.Default.WidgetDispatchResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new WidgetDispatchResponse { Ok = false, Error = ex.Message },
                    AppJsonContext.Default.WidgetDispatchResponse, statusCode: 500);
            }
        }).AllowPanel();

        app.MapPost("/widgets-api/proxy",
            async (WidgetProxyRequest body, WidgetProxyService proxy, HttpContext ctx) =>
        {
            var result = await proxy.ExecuteAsync(body, ctx.RequestAborted);
            return Results.Json(result, AppJsonContext.Default.WidgetProxyResponse);
        }).AllowPanel();

        // (The legacy GET /widgets-api/installed/{id}/worker.js route was
        // removed — Tier 2 workers always boot through a per-spawn code
        // session URL `/widgets-api/code/{sessionId}/worker.js`, so the
        // installed-route variant served only to widen the attack surface.)

        // Module-worker code session. The web side POSTs here to mint a
        // short-lived URL-path token; subsequent `import "./lib/x.js"`
        // statements inside the worker inherit the token automatically
        // (relative imports use the worker's base URL). The token is the
        // *only* auth the code-serving GET below requires - the Bearer
        // header can't ride along on module imports.
        app.MapPost("/widgets-api/installed/{id}/code-session",
            (string id, HttpContext ctx, WidgetRegistry registry, WidgetCodeSessionService sessions) =>
        {
            if (!WidgetIds.IsValid(id))
                return Results.Json(new WidgetCodeSessionResponse { Error = "invalid widget id" },
                    AppJsonContext.Default.WidgetCodeSessionResponse, statusCode: 400);
            if (!registry.TryGet(id, out var entry))
                return Results.Json(new WidgetCodeSessionResponse { Error = "widget not installed" },
                    AppJsonContext.Default.WidgetCodeSessionResponse, statusCode: 404);
            if (!entry.Manifest.Capabilities.WorkerCode)
                return Results.Json(new WidgetCodeSessionResponse { Error = "widget has no worker code capability" },
                    AppJsonContext.Default.WidgetCodeSessionResponse, statusCode: 400);
            var token = sessions.Create(id);
            return Results.Json(new WidgetCodeSessionResponse
            {
                SessionId = token,
                BaseUrl = "/widgets-api/code/" + token,
                ExpiresInSeconds = (int)WidgetCodeSessionService.DefaultLifetime.TotalSeconds,
            }, AppJsonContext.Default.WidgetCodeSessionResponse);
        }).AllowPanel();

        // Code-serving endpoint. NOT .AllowPanel() - authenticates via the
        // session token in the URL path itself, which is why module-worker
        // sibling imports work without needing a Bearer header. Validation
        // gates: session must exist + not be expired, widget must still be
        // installed, path must resolve under the widget root, extension
        // must be in AllowedCodeExtensions. The path-prefix bypass for this
        // route lives in Program.cs auth middleware.
        app.MapGet("/widgets-api/code/{sessionId}/{**path}",
            (string sessionId, string? path, HttpContext ctx,
             WidgetCodeSessionService sessions, WidgetRegistry registry) =>
            ServeCodeFile(sessionId, path, ctx, sessions, registry));
    }

    private static IResult ServeCodeFile(
        string sessionId,
        string? path,
        HttpContext ctx,
        WidgetCodeSessionService sessions,
        WidgetRegistry registry)
    {
        if (string.IsNullOrEmpty(path)) return Results.NotFound();
        var widgetId = sessions.Resolve(sessionId);
        if (widgetId is null) return Results.NotFound();
        if (!registry.TryGet(widgetId, out var entry)) return Results.NotFound();
        // The capability check from session-create is enforced again here so
        // a session for a widget that lost the capability mid-session stops
        // serving.
        if (!entry.Manifest.Capabilities.WorkerCode) return Results.NotFound();

        var resolved = ResolveBundleFile(entry.RootPath, path);
        if (resolved is null) return Results.NotFound();
        var ext = Path.GetExtension(resolved);
        if (!AllowedCodeExtensions.Contains(ext)) return Results.NotFound();

        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";

        var contentType = ext.ToLowerInvariant() switch
        {
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".json"         => "application/json; charset=utf-8",
            _               => "application/octet-stream",
        };
        return Results.Stream(File.OpenRead(resolved), contentType);
    }

    private static IResult ServeAsset(
        string id,
        string? path,
        HttpContext ctx,
        WidgetRegistry registry,
        bool requireImage)
    {
        if (!WidgetIds.IsValid(id)) return Results.NotFound();
        if (!registry.TryGet(id, out var entry)) return Results.NotFound();
        if (string.IsNullOrEmpty(path)) return Results.NotFound();

        var resolved = ResolveBundleFile(entry.RootPath, path);
        if (resolved is null) return Results.NotFound();

        if (requireImage)
        {
            var ext = Path.GetExtension(resolved);
            if (!AllowedAssetExtensions.Contains(ext)) return Results.NotFound();
        }

        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.CacheControl = "no-store";

        var contentType = Path.GetExtension(resolved).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return Results.Stream(File.OpenRead(resolved), contentType);
    }

    /// <summary>
    /// Resolve a bundle-relative path to an absolute path on disk. Returns
    /// null if the path escapes the bundle root, points at a symlink, or
    /// doesn't exist. Critical for blocking <c>../</c> traversal smuggled
    /// inside the URL and for refusing reparse-point exfiltration paths
    /// installed by a tampered or malicious bundle.
    /// </summary>
    internal static string? ResolveBundleFile(string root, string requested)
    {
        if (string.IsNullOrEmpty(requested)) return null;
        var rootFull = Path.GetFullPath(root);
        if (Path.IsPathRooted(requested)) return null;
        if (requested.Contains("..", StringComparison.Ordinal)) return null;

        var combined = Path.GetFullPath(Path.Combine(rootFull, requested));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal)) return null;
        if (!File.Exists(combined)) return null;

        try
        {
            var info = new FileInfo(combined);
            if (info.LinkTarget is not null) return null;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        }
        catch (IOException)
        {
            return null;
        }
        return combined;
    }

    private static bool IsBundleRelativeAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        return true;
    }

    private static WidgetInstalledListing BuildListing(WidgetEntry entry)
    {
        // Icons / SVG assets are now served from `/widgets-api/installed/{id}/asset/...`,
        // not the (removed) per-widget origin. Building the URL here keeps the
        // dashboard from having to know the route shape.
        var iconUrl = IsBundleRelativeAssetPath(entry.Manifest.Icon)
            ? $"/widgets-api/installed/{entry.Id}/asset/{entry.Manifest.Icon}"
            : null;

        var source = entry.Source switch
        {
            WidgetInstallPaths.Source.Dev => "dev",
            WidgetInstallPaths.Source.User => "user",
            WidgetInstallPaths.Source.Bundled => "bundled",
            _ => "unknown",
        };

        return new WidgetInstalledListing
        {
            Id = entry.Id,
            Name = entry.Manifest.Name,
            Version = entry.Manifest.Version,
            Description = entry.Manifest.Description,
            IconUrl = iconUrl,
            Surfaces = new List<string>(entry.Manifest.Surfaces),
            Capabilities = entry.Manifest.Capabilities,
            Viewport = entry.Manifest.Viewport,
            Settings = new List<WidgetManifestSettingEntry>(entry.Manifest.Settings),
            Sizes = new List<string>(entry.Manifest.Sizes),
            DefaultSize = entry.Manifest.DefaultSize,
            View = entry.Manifest.View,
            Data = entry.Manifest.Data,
            Fonts = entry.Manifest.Fonts,
            Local = entry.Manifest.Local,
            Source = source,
            Trusted = entry.Source != WidgetInstallPaths.Source.Dev,
        };
    }
}
