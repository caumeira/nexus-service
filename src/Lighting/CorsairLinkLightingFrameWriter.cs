using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the iCUE LINK System Hub at 30 Hz.
/// Builds one contiguous RGB byte buffer from all LED-bearing devices in
/// ascending channel order and calls <see cref="CorsairLinkHub.SendColors"/>
/// once per tick. The hub handles chunked endpoint writes internally.
///
/// Wire color order is R,G,B (no swap) per the protocol spec.
/// </summary>
public sealed class CorsairLinkLightingFrameWriter : IHostedService, IDisposable
{
    // 30 Hz, matching the engine's default frame interval.
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly CorsairLinkHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Per-device segment composition buffer: one buffer per segment, always
    // sized to one entry (each LINK device exposes exactly one segment).
    private RgbColor[][] _segBuf = Array.Empty<RgbColor[]>();

    // Scratch buffer for the concatenated wire frame; resized on demand.
    private byte[] _wireBuf = Array.Empty<byte>();
    private int _diagTick;

    public CorsairLinkLightingFrameWriter(
        LightingEngine engine, CorsairLinkHub hub, IConfigStore store, Np50IdentifyTracker identify)
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
                Console.Error.WriteLine($"[corsair-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
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

        // Count total LEDs to size the wire buffer.
        var hubDevices = _hub.State.Devices;
        var totalLeds = 0;
        foreach (var dev in hubDevices)
        {
            if (dev.LedCount > 0) totalLeds += dev.LedCount;
        }
        if (totalLeds == 0) return;

        var totalBytes = totalLeds * 3;
        if (_wireBuf.Length < totalBytes) _wireBuf = new byte[Math.Max(totalBytes, 512)];

        var wireOffset = 0;
        foreach (var dev in hubDevices)
        {
            if (dev.LedCount <= 0) continue;
            var id = $"corsair:ch{dev.Channel}";
            var structure = CorsairLinkLightingDeviceProvider.BuildStructure(id, dev);
            var zones = ZoneResolution.Resolve(structure, settings);
            SegmentFrameComposer.EnsureBuffers(structure, ref _segBuf);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segBuf);

            // Segment 0 holds all LEDs for this device; copy as R,G,B (no swap).
            var buf = _segBuf[0];
            for (var i = 0; i < buf.Length; i++)
            {
                var c = buf[i];
                _wireBuf[wireOffset]     = c.R;
                _wireBuf[wireOffset + 1] = c.G;
                _wireBuf[wireOffset + 2] = c.B;
                wireOffset += 3;
            }
        }

        if (++_diagTick % 30 == 0 && wireOffset >= 3)
        {
            DeviceFrame? m = null;
            foreach (var f in devices) { if (f.Id == "corsair:ch1") { m = f; break; } }
            var mc = m is not null && m.LedBytes.Length >= 3 ? $"({m.LedBytes[0]},{m.LedBytes[1]},{m.LedBytes[2]})" : "NO-MATCH";
            var ids = string.Join(",", System.Linq.Enumerable.Select(devices, d => d.Id));
            ServiceLog.Info($"[corsair-diag] frame engineDevs={devices.Length} composedLED0=({_wireBuf[0]},{_wireBuf[1]},{_wireBuf[2]}) ch1EngineFrame={mc} ids=[{ids}]");
        }

        _hub.SendColors(new ReadOnlySpan<byte>(_wireBuf, 0, wireOffset));
    }
}
