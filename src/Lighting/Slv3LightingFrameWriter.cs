using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Streams engine frames to every bound SLV3 wireless fan chain as a
/// single-frame RF_RgbSync animation - the plan's "OpenRGB-style / live
/// direct mode" (plans/lianli-wireless-support.md section 2). There is no
/// firmware ROM-effect catalog exposed for wireless fans: every tick composes
/// each chain's resolved zone frames into a fan-major 40-LED-per-fan buffer
/// and pushes it through <see cref="Slv3Hub.SendRgbFrame"/> only when the
/// buffer content changed or the fan's last RX-confirmed effect_index no
/// longer matches what this writer sent (a dropped push), mirroring the
/// wired hub's firmware-signature drift re-assert.
/// </summary>
public sealed class Slv3LightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    // Static single-frame animation: the interval only matters if the
    // firmware were looping multiple frames, which direct mode never sends.
    private const int IntervalMs = 100;

    // SegmentFrameComposer already applies the zone's LightingDevicePrefs
    // brightness and the global brightness to the composed colors, so this
    // pass-through leaves Slv3RgbFrame's brightness formula a no-op and lets
    // its power cap alone act on the final wire bytes.
    private const int PassThroughBrightnessPercent = 100;

    private readonly LightingEngine _engine;
    private readonly Slv3Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Slv3LightingDeviceProvider _provider;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();
    private RgbColor[] _wireBuffer = Array.Empty<RgbColor>();

    // Per-MAC last pushed state: content hash + the effect_index we sent, so
    // a dropped push (RX-reported effect_index drifts from ours) resends
    // even when the desired color content is unchanged.
    private readonly Dictionary<string, (int Hash, string EffectIndexHex)> _lastSent = new();

    public Slv3LightingFrameWriter(
        LightingEngine engine, Slv3Hub hub, IConfigStore store, Np50IdentifyTracker identify, Slv3LightingDeviceProvider provider)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _provider = provider;
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
                Console.Error.WriteLine($"[lianli-wireless-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
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

    internal void Tick()
    {
        if (!_hub.IsConnected)
        {
            _lastSent.Clear();
            return;
        }
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var nowTicks = DateTime.UtcNow.Ticks;

        var structures = _provider.BuildStructures();
        var liveMacs = new HashSet<string>(structures.Count);

        foreach (var structure in structures)
        {
            var macHex = Slv3LightingDeviceProvider.MacFromDeviceId(structure.DeviceId);
            if (macHex.Length == 0) continue;
            liveMacs.Add(macHex);

            var zones = ZoneResolution.Resolve(structure, settings);
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            var ringLen = Slv3LightingDeviceProvider.LedsPerFanPerRing;
            var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / ringLen;
            if (fanCount <= 0) continue;
            var totalLeds = fanCount * Slv3RgbFrame.LedsPerFan;
            EnsureWireBuffer(totalLeds);

            var inner = _segmentBuffers[Slv3LightingDeviceProvider.InnerSegment];
            var outer = _segmentBuffers[Slv3LightingDeviceProvider.OuterSegment];
            for (var f = 0; f < fanCount; f++)
            {
                var baseIdx = f * Slv3RgbFrame.LedsPerFan;
                for (var i = 0; i < ringLen; i++)
                {
                    _wireBuffer[baseIdx + i] = inner[f * ringLen + i];
                    _wireBuffer[baseIdx + ringLen + i] = outer[f * ringLen + i];
                }
            }

            var frameSpan = _wireBuffer.AsSpan(0, totalLeds);
            var hash = ComputeHash(frameSpan);

            // A dictionary miss defaults the tuple's string component to null,
            // not "" - IsNullOrEmpty covers both the miss and a not-yet-sent state.
            _lastSent.TryGetValue(macHex, out var last);
            var confirmed = FindConfirmedEffectIndex(macHex);
            var driftedSinceLastConfirm = !string.IsNullOrEmpty(last.EffectIndexHex)
                && confirmed.Length > 0
                && !string.Equals(confirmed, last.EffectIndexHex, StringComparison.OrdinalIgnoreCase);
            if (last.Hash == hash && !driftedSinceLastConfirm)
            {
                continue;
            }

            if (_hub.SendRgbFrame(macHex, frameSpan, PassThroughBrightnessPercent, IntervalMs, out var sentEffectIndexHex))
            {
                _lastSent[macHex] = (hash, sentEffectIndexHex);
            }
        }

        if (_lastSent.Count > liveMacs.Count)
        {
            var stale = new List<string>();
            foreach (var mac in _lastSent.Keys)
            {
                if (!liveMacs.Contains(mac)) stale.Add(mac);
            }
            foreach (var mac in stale)
            {
                _lastSent.Remove(mac);
            }
        }
    }

    private void EnsureWireBuffer(int totalLeds)
    {
        if (_wireBuffer.Length < totalLeds)
        {
            _wireBuffer = new RgbColor[totalLeds];
        }
    }

    private string FindConfirmedEffectIndex(string macHex)
    {
        var fans = _hub.State.Fans;
        for (var i = 0; i < fans.Length; i++)
        {
            if (string.Equals(fans[i].Mac, macHex, StringComparison.OrdinalIgnoreCase))
            {
                return fans[i].EffectIndex;
            }
        }
        return "";
    }

    private static int ComputeHash(ReadOnlySpan<RgbColor> leds)
    {
        var hc = new HashCode();
        for (var i = 0; i < leds.Length; i++)
        {
            hc.Add(leds[i].R);
            hc.Add(leds[i].G);
            hc.Add(leds[i].B);
        }
        return hc.ToHashCode();
    }
}
