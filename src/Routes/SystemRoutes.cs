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

        // No REST sensor endpoints - all hardware sensor / model data is
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

        // OS accent for the web's "system" accent source. Windows/macOS push it
        // from their native shell; Linux has no shell (the dashboard is a
        // browser) so the service reads the XDG portal and serves it here.
        app.MapGet("/system/accent", (ISystemAccentProvider accent) =>
            new SystemAccentResponse { Accent = accent.GetAccentHex() ?? "" }).AllowPanel();

        // Boot id for the web UI once-per-boot greeting check. Derived from OS
        // uptime (Environment.TickCount64), not process start, so it survives a
        // service restart within the same boot. Quantized so tick jitter does not
        // shift it; still wall-clock-derived, so a clock step mid-boot can change
        // it (at worst a repeat greeting).
        app.MapGet("/system/boot", () =>
        {
            var bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var quantizedSeconds = (bootTime.ToUnixTimeSeconds() / 60) * 60;
            return new SystemBootResponse { BootId = quantizedSeconds.ToString() };
        }).AllowPanel();

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
            // Clipboard-set then paste - the only Unicode-reliable cross-platform
            // path. Clobbers the clipboard (restore deferred). Cmd+V on macOS, Ctrl+V elsewhere.
            if (!clipboard.SetText(text))
                return ApiResponse.Fail("clipboard unavailable");
            inputter.Send(PasteChord());
            return ApiResponse.Ok();
        }).AllowPanel();

        // ── Open URL / file / folder / OS settings (deck launch actions) ──
        app.MapPost("/system/open-settings", async (IServiceProvider sp) =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
#if WINDOWS
                    var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                    if (registry?.GetAny() is not null)
                    {
                        await Nexus.Service.Helper.Domains.SystemCommands.OpenSettingsAsync(registry).ConfigureAwait(false);
                        return ApiResponse.Ok("opened");
                    }
                    if (!Environment.UserInteractive)
                    {
                        return ApiResponse.Fail("no interactive user session");
                    }
#endif
                    Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                    return ApiResponse.Ok("opened");
                }
                if (OperatingSystem.IsMacOS())
                {
                    var exit = ShellExecutor.RunExit("open", 5000, "-b", "com.apple.systempreferences");
                    return exit == 0 ? ApiResponse.Ok("opened") : ApiResponse.Fail("failed to open settings");
                }
                // Linux: fire-and-forget the first launcher that starts. Settings
                // apps are long-lived GUIs that never exit, so we must NOT wait on
                // them (RunExit would block, then kill the window it just opened).
                // Process.Start throws when the binary is absent - try the next.
                string[] linuxCandidates = ["gnome-control-center", "systemsettings5", "systemsettings"];
                foreach (var candidate in linuxCandidates)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = false });
                        return ApiResponse.Ok("opened");
                    }
                    catch { /* launcher not installed - try the next */ }
                }
                return ApiResponse.Fail("no settings app found");
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail($"failed to open settings: {ex.Message}");
            }
        }).AllowPanel();

        app.MapPost("/system/open-url", async (OpenUrlRequest body, IServiceProvider sp) =>
        {
            var url = body.Url?.Trim() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                return ApiResponse.Fail("url is required");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return ApiResponse.Fail("invalid url - must be an absolute http or https URL");
            }

            try
            {
#if WINDOWS
                if (OperatingSystem.IsWindows())
                {
                    var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                    if (registry?.GetAny() is not null)
                    {
                        await Nexus.Service.Helper.Domains.SystemCommands.OpenUrlAsync(registry, parsed.AbsoluteUri).ConfigureAwait(false);
                        return ApiResponse.Ok("opened");
                    }
                    if (!Environment.UserInteractive)
                    {
                        return ApiResponse.Fail("no interactive user session");
                    }
                }
#endif
                await Task.CompletedTask.ConfigureAwait(false);
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
                return ApiResponse.Ok("opened");
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail($"failed to open url: {ex.Message}");
            }
        }).AllowPanel();

        // open-path is LAN-only (denied on the relay) - it opens arbitrary local files.
        app.MapPost("/system/open-path", async (OpenPathBody body, IServiceProvider sp) =>
        {
            var path = body.Path?.Trim() ?? "";
            if (string.IsNullOrEmpty(path))
            {
                return ApiResponse.Fail("path is required");
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return ApiResponse.Fail("path does not exist");
            }

#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                var helperConnected = registry?.GetAny() is not null;
                if (helperConnected)
                {
                    if (Directory.Exists(path))
                    {
                        if (await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry!, path).ConfigureAwait(false))
                        {
                            return ApiResponse.Ok("opened");
                        }

                        return ApiResponse.Fail("failed to open folder");
                    }

                    // Files: route through the helper so they open in the user session
                    // and the resulting window can be brought to the foreground.
                    await Nexus.Service.Helper.Domains.SystemCommands.OpenFileAsync(registry!, path).ConfigureAwait(false);
                    return ApiResponse.Ok("opened");
                }

                if (!Environment.UserInteractive)
                {
                    return ApiResponse.Fail("no interactive user session");
                }
            }
#endif
            await Task.CompletedTask.ConfigureAwait(false);
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
