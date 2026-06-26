using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the Lian Li Uni Hub at 30 Hz. Each
/// composed device's zones are scattered into its segment buffers via
/// <see cref="SegmentFrameComposer"/> (so any partition renders), then every
/// segment streams to the inner/outer channels its composition binds it to -
/// one channel per port, or every active port's channel when mirrored. A
/// color-data + effect-commit report per channel, then one frame-latch.
/// </summary>
public sealed class LianLiLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    // Raw RGB scratch for one channel: at most MaxFansPerPort fans *
    // LedsPerFanPerChannel * 3 bytes/LED. The hub re-encodes to the wire report.
    private readonly byte[] _channelBuf =
        new byte[LianLiProtocol.MaxFansPerPort * LianLiProtocol.LedsPerFanPerChannel * 3];

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

        var comp = LianLiZoneSupport.ReadComposition(settings, _hub.DeviceId);
        var composed = LianLiZoneSupport.Compose(_hub.DeviceId, comp, settings.Devices.LianLi);

        var pushedAny = false;
        foreach (var device in composed)
        {
            var structure = device.Structure;
            var zones = ZoneResolution.Resolve(structure, settings);
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            for (var seg = 0; seg < structure.Segments.Count; seg++)
            {
                var buf = _segmentBuffers[seg];
                var byteCount = buf.Length * 3;
                for (var i = 0; i < buf.Length; i++)
                {
                    var c = buf[i];
                    var off = i * 3;
                    _channelBuf[off] = c.R;
                    _channelBuf[off + 1] = c.G;
                    _channelBuf[off + 2] = c.B;
                }
                foreach (var ch in device.SegmentChannels[seg])
                {
                    _hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount));
                    _hub.SendEffectCommit(ch);
                }
                pushedAny = true;
            }
        }

        if (pushedAny)
        {
            _hub.SendFrameLatch();
        }
    }
}
