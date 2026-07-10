using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Microsoft.AspNetCore.Mvc;

namespace Nexus.Service.Routes;

public static class SystemRoutes
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
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
        app.MapPost("/system/input/keys", (SendKeysBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SendKeys(body)).AllowPanel();

        app.MapPost("/system/input/text", (SendTextBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SendText(body.Text ?? "")).AllowPanel();

        // ── Open URL / file / folder / OS settings (deck launch actions) ──
        app.MapPost("/system/open-settings", (Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenSettingsAsync()).AllowPanel();

        app.MapPost("/system/open-url", (OpenUrlRequest body, Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenUrlAsync(body.Url ?? "")).AllowPanel();

        // open-path is denied on the relay (RelayHttpAllowlist.cs) since it
        // opens arbitrary local files, but is reachable from a paired phone
        // directly over LAN.
        app.MapPost("/system/open-path", (OpenPathBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.OpenPathAsync(body.Path ?? "")).AllowPanel();

        // ── Power / session. lock + sleep are panel/relay-reachable; shutdown /
        // restart / logout are LAN-only (no AllowPanel + denied on the relay). ──
        app.MapPost("/system/power/lock", (Nexus.Service.Actions.SystemActions actions) => actions.Lock() ? ApiResponse.Ok() : ApiResponse.Fail("lock failed")).AllowPanel();
        app.MapPost("/system/power/sleep", (Nexus.Service.Actions.SystemActions actions) => actions.Sleep() ? ApiResponse.Ok() : ApiResponse.Fail("sleep failed")).AllowPanel();
        app.MapPost("/system/power/shutdown", (Nexus.Service.Actions.SystemActions actions) => actions.Shutdown() ? ApiResponse.Ok() : ApiResponse.Fail("shutdown failed"));
        app.MapPost("/system/power/restart", (Nexus.Service.Actions.SystemActions actions) => actions.Restart() ? ApiResponse.Ok() : ApiResponse.Fail("restart failed"));
        app.MapPost("/system/power/logout", (Nexus.Service.Actions.SystemActions actions) => actions.Logout() ? ApiResponse.Ok() : ApiResponse.Fail("logout failed"));

        // ── Audio device enumeration + default switching ──
        app.MapGet("/system/audio/devices", (IAudioDeviceProvider a) => a.ListDevices()).AllowPanel();
        app.MapPost("/system/audio/default-output", (SetAudioDefaultBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SetDefaultOutput(body.DeviceId) ? ApiResponse.Ok() : ApiResponse.Fail("failed to set output device")).AllowPanel();
        app.MapPost("/system/audio/default-input", (SetAudioDefaultBody body, Nexus.Service.Actions.SystemActions actions) =>
            actions.SetDefaultInput(body.DeviceId) ? ApiResponse.Ok() : ApiResponse.Fail("failed to set input device")).AllowPanel();
    }
}
