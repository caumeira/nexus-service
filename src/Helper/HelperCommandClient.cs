#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Displays;
using Qos.Service.Models.Lighting;
using Qos.Service.Serialization;

namespace Qos.Service.Helper;

/// <summary>
/// Typed service-side facade over HelperRegistry. Each domain (tray icon,
/// brightness, volume, input injection, ...) gets a method here. The body
/// looks up the active helper and delegates to HelperConnection. New
/// commands: add a payload type to HelperMessages, register it in
/// AppJsonContext, add a method here. No envelope plumbing to touch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperCommandClient
{
    private readonly HelperRegistry _registry;

    public HelperCommandClient(HelperRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Set tray-icon visibility on the helper. Fire-and-forget; the tray
    /// is purely presentation, no caller needs a confirmation today.
    /// </summary>
    public Task SetTrayVisibleAsync(bool visible, CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.setVisible",
            payload: new TraySetVisiblePayload { Visible = visible },
            payloadType: AppJsonContext.Default.TraySetVisiblePayload,
            ct: ct);
    }

    /// <summary>
    /// Tell the helper that the service is shutting down: it should close
    /// the standalone --app window and exit so the tray icon disappears.
    /// Fired from the service's ApplicationStopping hook so both the
    /// settings "Stop Qos" path and the tray "Shut down" path (which goes
    /// through SCM) converge on the same teardown UX. No-op when no helper
    /// is currently connected.
    /// </summary>
    public Task SendShutdownAsync(CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "helper.shutdown",
            payload: new HelperShutdownPayload(),
            payloadType: AppJsonContext.Default.HelperShutdownPayload,
            ct: ct);
    }

    /// <summary>
    /// Nudge the user-session helper to wake the qos-overlay marshaler so
    /// it re-reads preferences immediately. Fire-and-forget; the overlay's
    /// 5s prefs poll is still the safety net if the helper isn't connected
    /// or the message gets dropped.
    /// </summary>
    public Task NotifyOverlayPrefsChangedAsync(CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "overlay.prefsChanged",
            payload: new OverlayPrefsChangedPayload(),
            payloadType: AppJsonContext.Default.OverlayPrefsChangedPayload,
            ct: ct);
    }

    /// <summary>
    /// Fire a media transport command (play / pause / next / ...) at the
    /// helper's GSMTC session. Fire-and-forget; we don't surface failures
    /// back into the HTTP route caller.
    /// </summary>
    public Task MediaControlAsync(string source, string action, CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "media.control",
            payload: new MediaControlPayload { Source = source, Action = action },
            payloadType: AppJsonContext.Default.MediaControlPayload,
            ct: ct);
    }

    /// <summary>
    /// Request album-art bytes for a session. Synchronous-shaped because
    /// the existing IMediaProvider.GetAlbumArt contract is sync; the route
    /// handler blocks on this. Returns empty bytes if the helper isn't
    /// connected or the session has no art.
    /// </summary>
    public async Task<byte[]> GetAlbumArtAsync(string source, CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Array.Empty<byte>();
        var result = await conn.SendCommandAsync(
            type: "media.getAlbumArt",
            payload: new AlbumArtRequest { Source = source },
            payloadType: AppJsonContext.Default.AlbumArtRequest,
            timeoutMs: 4000,
            ct: ct).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null) return Array.Empty<byte>();
        try
        {
            var art = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.AlbumArtResult);
            return art?.Bytes ?? Array.Empty<byte>();
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>Display brightness RPC. Each call blocks until the helper replies (or the configured timeout). Returns sensible defaults when no helper is connected.</summary>
    public async Task<string> BrightnessHintAsync(CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.hint", new DisplayBrightnessRequest(), AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.StringResult);
        return dto?.Value ?? "";
    }

    public async Task<IReadOnlyList<DisplayDto>> BrightnessEnumerateAsync(CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.enumerate", new DisplayBrightnessRequest(), AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.DisplayListResult);
        return dto?.Displays ?? new List<DisplayDto>();
    }

    public async Task<int?> BrightnessGetAsync(string id, CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.get", new DisplayBrightnessRequest { Id = id }, AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.NullableIntResult);
        return dto is null || !dto.HasValue ? null : dto.Value;
    }

    public async Task<DisplayBrightnessDto> BrightnessSetAsync(string id, int percent, CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.set", new DisplayBrightnessRequest { Id = id, Percent = percent }, AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        return ReadResult(r, AppJsonContext.Default.DisplayBrightnessDto) ?? new DisplayBrightnessDto();
    }

    public async Task<DisplayBrightnessWritePolicy> BrightnessPolicyAsync(string id, CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.policy", new DisplayBrightnessRequest { Id = id }, AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        return ReadResult(r, AppJsonContext.Default.DisplayBrightnessWritePolicy) ?? new DisplayBrightnessWritePolicy();
    }

    public async Task<DisplayVcpDto?> BrightnessGetVcpAsync(string id, byte code, CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.getVcp", new DisplayBrightnessRequest { Id = id, Code = code }, AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.DisplayVcpResult);
        return dto is null || !dto.Ok ? null : dto.Dto;
    }

    public async Task<bool> BrightnessSetVcpAsync(string id, byte code, int value, CancellationToken ct = default)
    {
        var r = await InvokeAsync("displayBrightness.setVcp", new DisplayBrightnessRequest { Id = id, Code = code, Value = value }, AppJsonContext.Default.DisplayBrightnessRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.BoolResult);
        return dto?.Value ?? false;
    }

    /// <summary>
    /// Enumerate attached monitors via the helper. DXGI cannot see desktop
    /// outputs from Session 0, so the helper (running in the interactive
    /// user session) is the source of truth. Returns empty when no helper
    /// is connected.
    /// </summary>
    public async Task<List<ScreenSyncMonitor>> MonitorEnumerateAsync(CancellationToken ct = default)
    {
        var r = await InvokeAsync("monitor.enumerate", new MonitorEnumerateRequest(), AppJsonContext.Default.MonitorEnumerateRequest, ct).ConfigureAwait(false);
        var dto = ReadResult(r, AppJsonContext.Default.MonitorListResult);
        return dto?.Monitors ?? new List<ScreenSyncMonitor>();
    }

    /// <summary>
    /// Start helper-side screen capture. Same Session 0 limitation as
    /// enumeration: <see cref="IDXGIOutputDuplication"/> cannot see desktop
    /// outputs under LocalSystem, so the helper does the capture in Session 1
    /// and pushes canvas-resolution frames back via
    /// <c>screenMirror.frame</c> envelopes. Fire-and-forget (no Id, the
    /// frames themselves are the success signal); no-op when no helper.
    /// </summary>
    public Task ScreenCaptureStartAsync(string monitorId, int width, int height, CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "screenMirror.start",
            payload: new ScreenMirrorStartPayload { MonitorId = monitorId, Width = width, Height = height },
            payloadType: AppJsonContext.Default.ScreenMirrorStartPayload,
            ct: ct);
    }

    /// <summary>Stop helper-side screen capture. Fire-and-forget. Helper releases DXGI resources and the capture thread exits.</summary>
    public Task ScreenCaptureStopAsync(CancellationToken ct = default)
    {
        var conn = _registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "screenMirror.stop",
            payload: new ScreenMirrorStopPayload(),
            payloadType: AppJsonContext.Default.ScreenMirrorStopPayload,
            ct: ct);
    }

    private async Task<HelperResult?> InvokeAsync<TPayload>(string type, TPayload payload, JsonTypeInfo<TPayload> payloadType, CancellationToken ct)
    {
        var conn = _registry.GetAny();
        if (conn is null) return null;
        return await conn.SendCommandAsync(type, payload, payloadType, timeoutMs: 4000, ct: ct).ConfigureAwait(false);
    }

    private static T? ReadResult<T>(HelperResult? r, JsonTypeInfo<T> typeInfo)
    {
        if (r is null || !r.Ok || r.Payload is null) return default;
        try { return JsonSerializer.Deserialize(r.Payload.Value, typeInfo); }
        catch { return default; }
    }
}
#endif
