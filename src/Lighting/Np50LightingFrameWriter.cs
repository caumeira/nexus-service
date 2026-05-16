using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Qos.Service.Lighting.Engine;
using Qos.Service.Peripherals.Hyte.Np50;
using Qos.Service.Persistence;

namespace Qos.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the NP50 hub. Mirrors what
/// <see cref="Rgb.RgbBridge"/> does for OpenRGB devices, except instead of
/// PushFrameAsync over the OpenRGB SDK we batch by Nexus Link port and
/// call <see cref="Np50Hub.WriteLighting"/>.
///
/// The engine fires <see cref="LightingEngine.OnFrame"/> at ~30 fps after
/// <see cref="LightingEngine.SampleDevicesFromCanvas"/> has filled each
/// DeviceFrame's per-LED RGB bytes. We walk <see cref="LightingEngine.Devices"/>,
/// pick the np50:* frames in id order, assemble one buffer per port (with
/// the 6-LED hub logo prefixed onto port 1's stream), and write each port
/// in a single command. Brightness / disabled / identify are honored
/// against the same settings store OpenRGB devices read from.
/// </summary>
public sealed class Np50LightingFrameWriter : IHostedService, IDisposable
{
    private const int IdentifyFlashHalfPeriodMs = 250;
    private const string LogoIdSuffix = ":logo";
    private const string PortIdInfix = ":port";

    private readonly LightingEngine _engine;
    private readonly Np50Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private Action<ReadOnlyMemory<byte>>? _frameHandler;

    // Per-port pending buffers, allocated lazily on first write so we don't
    // hold storage for empty ports.
    private readonly RgbColor[]?[] _portBuffers = new RgbColor[Np50Protocol.PortCount][];

    public Np50LightingFrameWriter(LightingEngine engine, Np50Hub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _frameHandler = OnFrame;
        _engine.OnFrame += _frameHandler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_frameHandler is not null) _engine.OnFrame -= _frameHandler;
        _frameHandler = null;
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    // Per-frame staging structures. Allocated once, cleared each tick.
    private readonly List<DeviceFrame>[] _stripsByPort =
    {
        new List<DeviceFrame>(),
        new List<DeviceFrame>(),
        new List<DeviceFrame>(),
    };
    private DeviceFrame? _logoFrame;

    private void OnFrame(ReadOnlyMemory<byte> frameMem)
    {
        // The serialized engine frame blob is unused — we read the per-device
        // LED bytes off DeviceFrame.LedBytes directly, which is also the
        // canonical post-canvas-sample view.
        _ = frameMem;
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var devicePrefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        // Classify each NP50 device frame this tick. We expect the engine's
        // device array to be the union of OpenRGB frames and our contributor
        // frames; the latter are already in port-then-daisy-chain order from
        // Np50LightingDeviceProvider.BuildFrames, so a single pass preserves
        // that order without sorting.
        _logoFrame = null;
        foreach (var l in _stripsByPort) l.Clear();
        for (var i = 0; i < devices.Length; i++)
        {
            var dev = devices[i];
            if (!IsNp50Id(dev.Id)) continue;
            if (dev.LedCount <= 0) continue;
            var (port, isLogo) = ClassifyId(dev.Id);
            if (isLogo) { _logoFrame = dev; continue; }
            if (port < 1 || port > Np50Protocol.PortCount) continue;
            _stripsByPort[port - 1].Add(dev);
        }

        var anyTouched = _logoFrame is not null;
        for (var p = 1; p <= Np50Protocol.PortCount; p++)
        {
            var strips = _stripsByPort[p - 1];
            if (strips.Count == 0 && (p != 1 || _logoFrame is null)) continue;

            // Build the per-port LED buffer. Port 1 gets the 6-LED logo
            // prefix; ports 2/3 are just the daisy-chained strip LEDs.
            var total = (p == 1 && _logoFrame is not null ? Np50LightingDeviceProvider.LogoLedCount : 0);
            foreach (var s in strips) total += s.LedCount;
            if (total <= 0) continue;
            EnsurePortCapacity(p, total);

            var dstIdx = 0;
            if (p == 1 && _logoFrame is not null)
            {
                CopyIntoBuffer(_portBuffers[p - 1]!, dstIdx, _logoFrame,
                    Math.Min(_logoFrame.LedCount, Np50LightingDeviceProvider.LogoLedCount),
                    settings, disabled, devicePrefs, globalBrightness, nowTicks);
                dstIdx += Np50LightingDeviceProvider.LogoLedCount;
                anyTouched = true;
            }
            foreach (var strip in strips)
            {
                CopyIntoBuffer(_portBuffers[p - 1]!, dstIdx, strip, strip.LedCount,
                    settings, disabled, devicePrefs, globalBrightness, nowTicks);
                dstIdx += strip.LedCount;
                anyTouched = true;
            }

            // Send.
            var span = new ReadOnlySpan<RgbColor>(_portBuffers[p - 1], 0, total);
            _hub.WriteLighting(p, span);
        }

        // anyTouched is computed for future feature gates (e.g. skipping
        // writes when nothing changed); not currently acted on.
        if (!anyTouched) { /* no-op */ }
    }

    private void CopyIntoBuffer(RgbColor[] dst, int dstStart, DeviceFrame frame, int writeLen,
        QosSettings settings, IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var brightnessMul = ComputeBrightnessMul(frame.Id, disabled, prefs, globalBrightness);
        var hasIdentify = TryGetActiveIdentify(frame.Id, nowTicks, out var startTicks);
        FillBufferSlice(dst, dstStart, frame.LedBytes, writeLen, brightnessMul, hasIdentify, startTicks, nowTicks);
    }

    // ── Helpers ──

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);

    private static (int port, bool isLogo) ClassifyId(string id)
    {
        if (id.EndsWith(LogoIdSuffix, StringComparison.Ordinal)) return (0, true);
        var portIdx = id.IndexOf(PortIdInfix, StringComparison.Ordinal);
        if (portIdx < 0) return (0, false);
        // Format: np50:<serial>:port<N>:dev<M>
        var after = id.AsSpan(portIdx + PortIdInfix.Length);
        var colon = after.IndexOf(':');
        if (colon <= 0) return (0, false);
        return int.TryParse(after.Slice(0, colon), out var port) ? (port, false) : (0, false);
    }

    private double ComputeBrightnessMul(string id, IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs, float globalBrightness)
    {
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        int devBrightness;
        try { devBrightness = prefs.TryGetValue(id, out var pref) ? pref.Brightness : 100; }
        catch (InvalidOperationException) { devBrightness = 100; }
        return globalBrightness * Math.Clamp(devBrightness, 0, 100) / 100.0;
    }

    private bool TryGetActiveIdentify(string id, long nowTicks, out long startTicks)
        => _identify.TryGetActive(id, nowTicks, out startTicks);

    private static void FillBufferSlice(RgbColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new RgbColor(255, 255, 255) : new RgbColor(0, 0, 0);
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = default;
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new RgbColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new RgbColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }

    private void EnsurePortCapacity(int port, int total)
    {
        var idx = port - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < total)
        {
            _portBuffers[idx] = new RgbColor[Math.Max(total, 64)];
        }
    }
}
