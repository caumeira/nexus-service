using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the keeb over raw HID. Mirrors
/// <see cref="Np50LightingFrameWriter"/>: a 30 Hz timer walks
/// <see cref="LightingEngine.Devices"/>, composes the device's zone frames
/// into the keys + underglow segment buffers (applying brightness / disabled
/// / identify per zone card against the shared settings store), and streams
/// each hardware segment via <see cref="KeebHub"/>. With the default
/// partition this is byte-identical to the legacy per-card writer.
/// </summary>
public sealed class KeebLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33; // 30 Hz, matches the engine + NP50 writer.

    private readonly LightingEngine _engine;
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly KeebSettingsApplier _applier;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    public KeebLightingFrameWriter(LightingEngine engine, KeebHub hub, IConfigStore store, Np50IdentifyTracker identify, KeebSettingsApplier applier)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _applier = applier;
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
                Console.Error.WriteLine($"[keeb-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_hub.IsConnected) return;

        // Firmware/software arbitration: stream only while a software effect is
        // active. When it stops, re-assert the persisted firmware settings once
        // so the onboard animation (which streaming suppressed) comes back -
        // matching the panel's "firmware lighting applies when nexus isn't
        // actively driving the LEDs".
        if (_engine.CurrentEffectName == "none")
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _applier.Apply();
            }
            return;
        }
        _wasStreaming = true;

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var hubId = _hub.DeviceId;
        if (string.IsNullOrEmpty(hubId)) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var structure = KeebZoneSupport.BuildStructure(hubId);
        var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settings);
        Nexus.Service.Lighting.Zones.SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
        var touched = Nexus.Service.Lighting.Zones.SegmentFrameComposer.Compose(
            structure, zones, devices, disabled, prefs, globalBrightness, nowTicks, _identify, _segmentBuffers);

        if (touched[KeebZoneSupport.KeysSegment])
            _hub.WriteKeyboard(_segmentBuffers[KeebZoneSupport.KeysSegment]);
        if (touched[KeebZoneSupport.UnderglowSegment])
            _hub.WriteSurround(_segmentBuffers[KeebZoneSupport.UnderglowSegment]);
    }
}
