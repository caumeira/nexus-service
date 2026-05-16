using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Qos.Service.Lighting.Engine;
using Qos.Service.Peripherals.Hyte.MiniHub;
using Qos.Service.Persistence;
using MiniHubColor = Qos.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Qos.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the MiniHub. Same pattern as
/// <see cref="Np50LightingFrameWriter"/>: own 30 Hz timer so disabled
/// zones still receive blank frames when no effect is running; reads
/// brightness / disabled / identify from the shared settings store.
/// One <c>WriteLighting</c> call per LED channel per tick.
/// </summary>
public sealed class MiniHubLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly MiniHubColor[]?[] _portBuffers = new MiniHubColor[MiniHubProtocol.LedPortCount][];

    public MiniHubLightingFrameWriter(LightingEngine engine, MiniHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
            { Console.Error.WriteLine($"[minihub-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

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
        // Channel 1 = port 3, channel 2 = port 4 per the spec table.
        TryPushZone(devices, $"{hubId}:port3", channel: 1, disabled, prefs, globalBrightness, nowTicks);
        if (_hub.State.Port4.LedCount > 0)
            TryPushZone(devices, $"{hubId}:port4", channel: 2, disabled, prefs, globalBrightness, nowTicks);
    }

    private void TryPushZone(DeviceFrame[] devices, string id, int channel,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }
        if (frame is null || frame.LedCount <= 0) return;

        var brightnessMul = ComputeBrightnessMul(id, disabled, prefs, globalBrightness);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < frame.LedCount)
            _portBuffers[idx] = new MiniHubColor[Math.Max(frame.LedCount, 64)];
        var dst = _portBuffers[idx]!;
        FillBufferSlice(dst, 0, frame.LedBytes, frame.LedCount, brightnessMul, hasIdentify, startTicks, nowTicks);
        _hub.WriteLighting(channel, new ReadOnlySpan<MiniHubColor>(dst, 0, frame.LedCount));
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness)
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

    private static void FillBufferSlice(MiniHubColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new MiniHubColor(255, 255, 255) : new MiniHubColor(0, 0, 0);
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
                dst[dstStart + i] = new MiniHubColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new MiniHubColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
