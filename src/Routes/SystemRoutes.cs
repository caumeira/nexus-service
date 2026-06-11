using System.Diagnostics;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Platform.Power;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Microsoft.AspNetCore.Mvc;

namespace Nexus.Service.Routes;

public static class SystemRoutes
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapPost("/system/polling-rate", (SetPollingRateBody body, [FromServices] ProcessMonitor pm, [FromServices] INetworkProvider net, [FromServices] MonitoringBroadcaster monitoring) =>
        {
            pm.SetInterval(body.PollingRate);
            net.SetInterval(body.PollingRate);
            monitoring.SetInterval(body.PollingRate);
            return ApiResponse.Ok();
        });

        app.MapGet("/system/elevation", () =>
        {
            var platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "macos"
                : "linux";
            var elevation = ProcessElevation.GetCurrent();
            return new ProcessElevationResponse
            {
                Platform = platform,
                Supported = elevation.Supported,
                IsElevated = elevation.IsElevated,
                Status = elevation.Status,
            };
        }).AllowPanel();

        app.MapPost("/system/elevation/relaunch", () =>
        {
            var result = ProcessRelauncher.TryRelaunchAsAdmin();
            return new ProcessElevationRelaunchResponse
            {
                Result = result switch
                {
                    RelaunchResult.Started => "started",
                    RelaunchResult.AlreadyElevated => "already-elevated",
                    RelaunchResult.Unsupported => "unsupported",
                    RelaunchResult.UserDenied => "user-denied",
                    _ => "failed",
                },
            };
        });

        // No REST sensor endpoints — all hardware sensor / model data is
        // delivered via the `/monitoring` topic over the multiplex WebSocket.
        // RAM capacity ships as `theoreticalMaximum` on the Memory Used sensor.

        // Compact, shareable rig identity for the Devices → System Specs tab.
        // Cached for the lifetime of the service (hardware specs don't change
        // at runtime); `SystemSpecsPrewarmService` populates the cache off
        // the boot critical path so the first request is in-memory.
        // Async so the first post-boot request waits for LHM's background open
        // to finish (~1-3 s) and returns fully-populated CPU/motherboard/GPU
        // names. Subsequent calls hit the cache and return in microseconds.
        app.MapGet("/system/specs", (SystemSpecsCollector collector, HttpContext ctx) =>
            collector.GetAsync(ctx.RequestAborted)).AllowPanel();

        // Volume (default render endpoint)
        app.MapGet("/system/volume", (IVolumeProvider v) => v.GetState()).AllowPanel();
        app.MapPost("/system/volume", (SetVolumeBody body, IVolumeProvider v, MultiplexHub hub) =>
        {
            v.SetVolume(body.Volume);
            PanelTopics.BroadcastVolume(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
        app.MapPost("/system/volume/mute", (SetMutedBody body, IVolumeProvider v, MultiplexHub hub) =>
        {
            v.SetMuted(body.Muted);
            PanelTopics.BroadcastVolume(hub);
            return ApiResponse.Ok();
        }).AllowPanel();

        // ── Keyboard / text injection (deck hotkey + type-text actions) ──
        app.MapGet("/system/input/status", () =>
        {
            var mac = OperatingSystem.IsMacOS();
            return new InputStatusResponse
            {
                Platform = OperatingSystem.IsWindows() ? "windows" : mac ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unsupported",
                Supported = OperatingSystem.IsWindows() || mac || OperatingSystem.IsLinux(),
                AccessibilityGranted = !mac || Nexus.Service.Peripherals.Keeb.MacInputter.AccessibilityGranted(),
            };
        }).AllowPanel();

        app.MapPost("/system/input/keys", (SendKeysBody body, IInputterProvider inputter) =>
        {
            var input = BuildKeyStrokes(body);
            if (input.Strokes.Count == 0)
                return ApiResponse.Fail("key or strokes required");
            inputter.Send(input);
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapPost("/system/input/text", (SendTextBody body, IInputterProvider inputter, IClipboardProvider clipboard) =>
        {
            var text = body.Text ?? "";
            if (text.Length == 0)
                return ApiResponse.Fail("text required");
            // Clipboard-set then paste — the only Unicode-reliable cross-platform
            // path. Clobbers the clipboard (restore deferred). Cmd+V on macOS, Ctrl+V elsewhere.
            if (!clipboard.SetText(text))
                return ApiResponse.Fail("clipboard unavailable");
            inputter.Send(PasteChord());
            return ApiResponse.Ok();
        }).AllowPanel();

        // ── Open URL / file / folder (deck launch actions) ──
        app.MapPost("/system/open-url", (OpenUrlRequest body) =>
        {
            var url = body.Url?.Trim() ?? "";
            if (string.IsNullOrEmpty(url))
                return ApiResponse.Fail("url is required");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return ApiResponse.Fail("invalid url - must be an absolute http or https URL");
            }
            try
            {
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
                return ApiResponse.Ok("opened");
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail($"failed to open url: {ex.Message}");
            }
        }).AllowPanel();

        // open-path is LAN-only (denied on the relay) — it opens arbitrary local files.
        app.MapPost("/system/open-path", (OpenPathBody body) =>
        {
            var path = body.Path?.Trim() ?? "";
            if (string.IsNullOrEmpty(path))
                return ApiResponse.Fail("path is required");
            if (!File.Exists(path) && !Directory.Exists(path))
                return ApiResponse.Fail("path does not exist");
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return ApiResponse.Ok("opened");
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail($"failed to open path: {ex.Message}");
            }
        }).AllowPanel();

        // ── Power / session. lock + sleep are panel/relay-reachable; shutdown /
        // restart / logout are LAN-only (no AllowPanel + denied on the relay). ──
        app.MapPost("/system/power/lock", (ISystemPowerProvider p) => p.Lock() ? ApiResponse.Ok() : ApiResponse.Fail("lock failed")).AllowPanel();
        app.MapPost("/system/power/sleep", (ISystemPowerProvider p) => p.Sleep() ? ApiResponse.Ok() : ApiResponse.Fail("sleep failed")).AllowPanel();
        app.MapPost("/system/power/shutdown", (ISystemPowerProvider p) => p.Shutdown() ? ApiResponse.Ok() : ApiResponse.Fail("shutdown failed"));
        app.MapPost("/system/power/restart", (ISystemPowerProvider p) => p.Restart() ? ApiResponse.Ok() : ApiResponse.Fail("restart failed"));
        app.MapPost("/system/power/logout", (ISystemPowerProvider p) => p.Logout() ? ApiResponse.Ok() : ApiResponse.Fail("logout failed"));

        // ── Audio device enumeration + default switching ──
        app.MapGet("/system/audio/devices", (IAudioDeviceProvider a) => a.ListDevices()).AllowPanel();
        app.MapPost("/system/audio/default-output", (SetAudioDefaultBody body, IAudioDeviceProvider a) =>
            a.SetDefaultOutput(body.DeviceId) ? ApiResponse.Ok() : ApiResponse.Fail("failed to set output device")).AllowPanel();
        app.MapPost("/system/audio/default-input", (SetAudioDefaultBody body, IAudioDeviceProvider a) =>
            a.SetDefaultInput(body.DeviceId) ? ApiResponse.Ok() : ApiResponse.Fail("failed to set input device")).AllowPanel();

        // Current desktop wallpaper for the dashboard's "wallpaper" background
        // mode. Windows + macOS; other platforms (and "no wallpaper set") return
        // 404 and the web falls back to the flat base. Desktop-token gated (no
        // AllowPanel) so paired phones can't pull the host's wallpaper.
        app.MapGet("/system/wallpaper", () =>
        {
            if (OperatingSystem.IsWindows())
            {
                var path = WallpaperReader.GetCurrentWallpaperPath();
                if (path is not null && File.Exists(path))
                    return Results.File(path, "image/jpeg");
            }
            else if (OperatingSystem.IsMacOS())
            {
                var wp = Nexus.Service.Platform.Mac.MacWallpaperReader.GetServable();
                if (wp is not null)
                    return Results.File(wp.Value.Path, wp.Value.ContentType);
            }
            return Results.NotFound();
        });
    }

    /// <summary>
    /// Builds the inputter strokes for a key request. An explicit Strokes list
    /// wins; otherwise a single chord is expanded to a key-down then key-up
    /// (both carrying the modifier flags) so the combo presses and releases.
    /// </summary>
    private static InputterBody BuildKeyStrokes(SendKeysBody body)
    {
        if (body.Strokes is { Count: > 0 })
            return new InputterBody { Strokes = body.Strokes };
        if (string.IsNullOrEmpty(body.Key))
            return new InputterBody();
        return new InputterBody
        {
            Strokes =
            {
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keydown" },
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keyup" },
            },
        };
    }

    private static InputterBody PasteChord()
    {
        var mac = OperatingSystem.IsMacOS();
        MacroStroke V(string type) => new()
        {
            Key = "KeyV",
            Meta = mac,
            Ctrl = !mac,
            Type = type,
        };
        return new InputterBody { Strokes = { V("keydown"), V("keyup") } };
    }
}
