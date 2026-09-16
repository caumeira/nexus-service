using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Drives the Galahad II LCD pump head through the panel hub that already owns MI_01.
/// Canvas only - the LCD firmware's own effect table diverges from the wired Trinity's
/// past mode 6 (see Galahad2LightingFrameWriter.BuildColorBytes / Galahad2LightingModes),
/// and the UI never offers a firmware mode for this device anyway (it only lists them for
/// lianli-aio), so there is no reachable, correctly-mapped non-canvas path to drive.
/// </summary>
public sealed class Galahad2LcdLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly JpegPanelHub _hub;
    private readonly IConfigStore _store;
    private readonly Galahad2LcdLightingDeviceProvider _provider;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();
    private readonly byte[] _wireColors = new byte[PerLedColorBytes];
    private readonly byte[] _lastWireColors = new byte[PerLedColorBytes];
    private bool _hasLastCanvas;
    private bool _lastWasCanvas;
    private byte? _lastCanvasBrightness;
    private bool _loggedNoCanvasFrame;
    private bool _loggedCanvasFailure;
    private bool _loggedCanvasSuccess;

    private const int PerLedColorBytes = Galahad2LcdLightingDeviceProvider.PumpLedCount * 3;

    public Galahad2LcdLightingFrameWriter(
        LightingEngine engine,
        JpegPanelHub hub,
        IConfigStore store,
        Galahad2LcdLightingDeviceProvider provider,
        FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _provider = provider;
        _gates = gates ?? FeatureGates.AllEnabled;
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
            catch { }
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
                Console.Error.WriteLine($"[galahad2-lcd-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
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
        if (!_gates.Lighting)
        {
            return;
        }
        if (!_hub.IsConnected)
        {
            ResetCaches();
            return;
        }

        var settings = _store.Load();
        var lighting = settings.Devices.Galahad2Lighting;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var brightnessRaw = (byte)Math.Clamp(
            (int)Math.Round(Math.Min((double)lighting.Brightness, globalBrightness * 4.0)), 0, 4);
        var structure = _provider.BuildStructure();
        var zones = ZoneResolution.Resolve(structure, settings);
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            ResetCaches();
            return;
        }

        if (lighting.Mode != "canvas")
        {
            // Not reachable from the UI (it only offers firmware modes for lianli-aio),
            // and there is no verified firmware-mode byte table for this device - see the
            // class doc. Treat anything else as "nothing to draw" rather than guess.
            ResetCaches();
            return;
        }
        if (_engine.Devices.Length == 0)
        {
            if (!_loggedNoCanvasFrame)
            {
                ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB skipped: lighting engine has no pump frame");
                _loggedNoCanvasFrame = true;
            }
            ResetCaches();
            return;
        }
        TickCanvas(structure, zones, settings, globalBrightness, brightnessRaw);
    }

    private void TickCanvas(
        DeviceStructure structure,
        IReadOnlyList<ResolvedZone> zones,
        NexusSettings settings,
        float globalBrightness,
        byte brightnessRaw)
    {
        SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
        SegmentFrameComposer.Compose(
            structure,
            zones,
            _engine.Devices,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.UncontrolledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            globalBrightness,
            masterMul: 1.0,
            nowTicks: DateTime.UtcNow.Ticks,
            identify: null,
            _segmentBuffers);

        var colors = _segmentBuffers[Galahad2LcdLightingDeviceProvider.PumpSegment];
        _wireColors.AsSpan().Clear();
        for (var i = 0; i < colors.Length; i++)
        {
            _wireColors[i * 3] = colors[i].R;
            _wireColors[i * 3 + 1] = colors[i].G;
            _wireColors[i * 3 + 2] = colors[i].B;
        }

        var brightnessChanged = !_lastCanvasBrightness.HasValue || _lastCanvasBrightness.Value != brightnessRaw;
        var needsBootstrap = !_lastWasCanvas || brightnessChanged;
        if (!needsBootstrap && _hasLastCanvas && _wireColors.AsSpan().SequenceEqual(_lastWireColors))
        {
            return;
        }
        // The controller ignores 0x14 until an A-command has put the pump channel in host RGB mode.
        if (needsBootstrap
            && !_hub.SendPumpZoneLighting(
                ring: 0,
                mode: 0x03,
                brightness: brightnessRaw,
                speed: 0,
                direction: 0,
                colors: _wireColors.AsSpan(0, 3)))
        {
            if (!_loggedCanvasFailure)
            {
                ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB bootstrap write rejected (0x83)");
                _loggedCanvasFailure = true;
            }
            return;
        }
        if (!_hub.SendPumpPerLed(_wireColors))
        {
            if (!_loggedCanvasFailure)
            {
                ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB per-LED write rejected (0x14)");
                _loggedCanvasFailure = true;
            }
            return;
        }
        if (!_loggedCanvasSuccess)
        {
            ServiceLog.Info("[lianli-galahad2-lcd] pump RGB canvas active (0x83 + 0x14, 12 LEDs)");
            _loggedCanvasSuccess = true;
        }
        _wireColors.AsSpan().CopyTo(_lastWireColors);
        _hasLastCanvas = true;
        _lastWasCanvas = true;
        _lastCanvasBrightness = brightnessRaw;
    }

    private void ResetCaches()
    {
        _hasLastCanvas = false;
        _lastWasCanvas = false;
        _lastCanvasBrightness = null;
    }
}
