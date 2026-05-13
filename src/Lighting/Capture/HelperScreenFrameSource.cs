#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using Qos.Service.Helper;
using Qos.Service.Helper.Domains;
using Qos.Service.Serialization;

namespace Qos.Service.Lighting.Capture;

/// <summary>
/// Service-side <see cref="IScreenFrameSource"/> that delegates capture to
/// the user-session helper. <see cref="Start"/> issues a
/// <c>screenMirror.start</c> RPC; the helper pushes canvas-sized RGB24
/// frames back via <c>screenMirror.frame</c> envelopes. We subscribe to
/// <see cref="HelperRegistry.InboundEnvelope"/> and cache the latest frame
/// so <see cref="ScreenMirrorEffect"/> can pull without blocking on the
/// pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperScreenFrameSource : IScreenFrameSource
{
    private readonly HelperRegistry _registry;
    private readonly object _lock = new();
    private byte[]? _latest;
    private int _latestW, _latestH;
    private int _expectedW, _expectedH;
    private bool _subscribed;

    public HelperScreenFrameSource(HelperRegistry registry)
    {
        _registry = registry;
    }

    public void Start(string monitorId, int width, int height)
    {
        EnsureSubscribed();
        lock (_lock)
        {
            _expectedW = width;
            _expectedH = height;
            // Drop any frame from a previous capture session - it's the wrong
            // size and would render garbage if blitted into the new canvas.
            _latest = null;
            _latestW = 0;
            _latestH = 0;
        }
        // Fire-and-forget: the pipe write runs through the helper's
        // SemaphoreSlim and could stall briefly if other domains are mid-
        // write. We're called from the engine render thread (via
        // ScreenMirrorEffect.EnsureStarted), so blocking here would freeze
        // the lighting loop. Frames start flowing once the helper acks
        // internally; until then TryAcquireFrame returns null and the
        // effect renders dark grey, which is the same behavior as
        // a stalled DXGI/ffmpeg startup.
        _ = ScreenMirrorCommands.StartAsync(_registry, monitorId, width, height);
    }

    public void Stop()
    {
        // Same fire-and-forget rationale as Start. Stop is invoked from
        // LightingEngine.SetEffect under its swap lock when the old effect
        // is disposed - blocking here can stall the next effect's
        // construction.
        _ = ScreenMirrorCommands.StopAsync(_registry);
        lock (_lock)
        {
            _latest = null;
            _latestW = 0;
            _latestH = 0;
        }
    }

    public byte[]? TryAcquireFrame(out int width, out int height)
    {
        lock (_lock)
        {
            width = _latestW;
            height = _latestH;
            return _latest;
        }
    }

    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        _registry.InboundEnvelope += OnEnvelope;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type != "screenMirror.frame" || env.Payload is null) return;
        // Skip the JSON decode entirely when no capture session is active.
        // The subscription stays registered for the lifetime of the singleton
        // so any in-flight frames from a late-arriving helper or a prior
        // session would otherwise pay the ~58 KB base64 decode cost.
        if (_expectedW == 0) return;
        try
        {
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenMirrorFramePayload);
            if (p is null) return;
            // Drop stale frames from a prior capture session - the canvas
            // resampling assumes the buffer size matches what we asked for.
            if (_expectedW != 0 && (p.Width != _expectedW || p.Height != _expectedH)) return;
            lock (_lock)
            {
                _latest = p.Bytes;
                _latestW = p.Width;
                _latestH = p.Height;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-frame-source] decode failed: {ex.Message}"); }
    }
}
#endif
