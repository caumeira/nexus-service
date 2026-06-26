using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the Lian Li Uni Hub at 30 Hz.
/// For each active channel, sends the color data output report, then
/// an effect-commit feature report. After all channels, sends the frame-latch
/// feature report.
/// </summary>
public sealed class LianLiLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LianLiLightingFrameWriter(LightingEngine engine, LianLiHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lianli-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // Raw RGB scratch for one channel: at most MaxFansPerPort fans *
    // LedsPerFanPerChannel * 3 bytes/LED. The frame writer copies engine LEDs
    // here, then the hub re-encodes to the 353-byte output report.
    private readonly byte[] _channelBuf =
        new byte[LianLiProtocol.MaxFansPerPort * LianLiProtocol.LedsPerFanPerChannel * 3];

    private void Tick()
    {
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;
        var hubId = _hub.DeviceId;

        var pushedAny = false;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            pushedAny |= TryPushChannel(devices, $"{hubId}:port{p}:inner", p * 2,
                disabled, prefs, globalBrightness, nowTicks);
            pushedAny |= TryPushChannel(devices, $"{hubId}:port{p}:outer", p * 2 + 1,
                disabled, prefs, globalBrightness, nowTicks);
        }

        if (pushedAny)
        {
            _hub.SendFrameLatch();
        }
    }

    private bool TryPushChannel(
        DeviceFrame[] devices, string id, int ch,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        {
            if (devices[i].Id == id)
            {
                frame = devices[i];
                break;
            }
        }
        if (frame == null) return false;

        var ledCount = frame.LedCount;
        var brightnessMul = ComputeBrightnessMul(id, disabled, prefs, globalBrightness);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var byteCount = ledCount * 3;
        var buf = _channelBuf;

        FillBufferSlice(buf, 0, frame.LedBytes, ledCount, brightnessMul, hasIdentify, startTicks, nowTicks);

        _hub.SendColorData(ch, buf.AsSpan(0, byteCount));
        _hub.SendEffectCommit(ch);
        return true;
    }

    private static double ComputeBrightnessMul(
        string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness)
    {
        if (disabled.Count > 0)
        {
            foreach (var d in disabled)
            {
                if (d == id) return 0.0;
            }
        }
        int devBrightness;
        try { devBrightness = prefs.TryGetValue(id, out var pref) ? pref.Brightness : 100; }
        catch (InvalidOperationException) { devBrightness = 100; }
        return globalBrightness * Math.Clamp(devBrightness, 0, 100) / 100.0;
    }

    private static void FillBufferSlice(
        byte[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var r = (byte)(on ? 255 : 0);
            var g = (byte)(on ? 255 : 0);
            var b = (byte)(on ? 255 : 0);
            for (var i = 0; i < ledCount; i++)
            {
                var off = dstStart + i * 3;
                if (off + 2 >= dst.Length) break;
                dst[off] = r; dst[off + 1] = g; dst[off + 2] = b;
            }
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var off = dstStart + i * 3;
                if (off + 2 >= dst.Length) break;
                dst[off] = 0; dst[off + 1] = 0; dst[off + 2] = 0;
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount; i++)
            {
                var srcOff = i * 3;
                if (srcOff + 2 >= src.Length) break;
                var dstOff = dstStart + i * 3;
                if (dstOff + 2 >= dst.Length) break;
                dst[dstOff] = src[srcOff];
                dst[dstOff + 1] = src[srcOff + 1];
                dst[dstOff + 2] = src[srcOff + 2];
            }
            return;
        }
        for (var i = 0; i < ledCount; i++)
        {
            var srcOff = i * 3;
            if (srcOff + 2 >= src.Length) break;
            var dstOff = dstStart + i * 3;
            if (dstOff + 2 >= dst.Length) break;
            dst[dstOff] = (byte)(src[srcOff] * brightnessMul);
            dst[dstOff + 1] = (byte)(src[srcOff + 1] * brightnessMul);
            dst[dstOff + 2] = (byte)(src[srcOff + 2] * brightnessMul);
        }
    }
}
