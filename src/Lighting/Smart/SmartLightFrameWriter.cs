using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Streams engine canvas color to the network lights while an effect runs.
/// Unlike the serial/HID hub writers (which push every tick to keep hardware
/// refreshed), this only streams when <see cref="LightingEngine.CurrentEffect"/>
/// is active — network devices hold their last state, and hammering them when
/// idle wastes bandwidth and trips rate limits. Per-device coalescing + rate
/// ceilings live in <see cref="NetworkSendThrottle"/> (via the provider), so a
/// 33 ms tick here can't outrun a slow bridge. When an effect stops, lamps are
/// returned to their configured static color once.
/// </summary>
public sealed class SmartLightFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly SmartLightProvider _provider;
    private readonly IConfigStore _store;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;

    public SmartLightFrameWriter(LightingEngine engine, SmartLightProvider provider, IConfigStore store)
    {
        _engine = engine;
        _provider = provider;
        _store = store;
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
                ServiceLog.Warn($"[smart-lights-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (_engine.CurrentEffect is null)
        {
            if (_wasStreaming)
            {
                _provider.RestoreStatic();
                _wasStreaming = false;
            }
            return;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var global = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var streamed = false;

        for (var i = 0; i < devices.Length; i++)
        {
            var frame = devices[i];
            if (!_provider.Owns(frame.Id)) continue;
            if (frame.LedCount <= 0) continue;
            if (disabled.Contains(frame.Id)) continue;

            var (r, g, b) = AverageRgb(frame.LedBytes, frame.LedCount);
            var devBrightness = prefs.TryGetValue(frame.Id, out var pref) ? pref.Brightness : 100;
            var b01 = global * Math.Clamp(devBrightness, 0, 100) / 100f;
            _provider.SubmitFrame(frame.Id, new LightFrame(On: true, r, g, b, b01));
            streamed = true;
        }

        if (streamed) _wasStreaming = true;
    }

    private static (byte r, byte g, byte b) AverageRgb(ReadOnlySpan<byte> leds, int ledCount)
    {
        if (ledCount <= 0 || leds.Length < 3) return (0, 0, 0);
        long sr = 0, sg = 0, sb = 0;
        var n = Math.Min(ledCount, leds.Length / 3);
        for (var i = 0; i < n; i++)
        {
            var off = i * 3;
            sr += leds[off]; sg += leds[off + 1]; sb += leds[off + 2];
        }
        if (n == 0) return (0, 0, 0);
        return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }
}
