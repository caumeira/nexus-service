using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Discovers and owns every connected Stream Deck's <see cref="IStreamDeckSurface"/>,
/// keyed by HID path. Surface-agnostic: real HID decks and (dev-tools-gated)
/// the shared simulated deck are tracked in the same dictionary and pumped
/// through the same input loop, so the worker never branches on transport.
///
/// Phase 0 only opens Gen1-protocol models (the bench-verified Mini and its
/// BMP siblings); Gen2 detection/connection lands in Phase 3. No action
/// dispatch yet - presses are logged only (Phase 1 wires the executor).
/// </summary>
public sealed class StreamDeckConnectionWorker : BackgroundService
{
    private const int TickMs = 1000;

    /// <summary>
    /// Per-surface read timeout inside one tick. Bounded so a tick with
    /// several tracked decks still completes well under TickMs; sub-second
    /// press latency isn't required until Phase 1 wires the executor.
    /// </summary>
    private const int InputPollTimeoutMs = 50;

    internal const string SimulatedKey = "sim";

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly SimulatedStreamDeckSurface? _simulated;
    private readonly Dictionary<string, IStreamDeckSurface> _surfaces = new();
    private readonly Dictionary<string, bool[]> _lastKeyStates = new();

    public StreamDeckConnectionWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        DeviceControlGate gate,
        SimulatedStreamDeckSurface? simulated = null)
    {
        _hid = hid;
        _presence = presence;
        _gate = gate;
        _simulated = simulated;
    }

    /// <summary>Every currently tracked surface, keyed by HID path (or "sim" for the simulated deck).</summary>
    public IReadOnlyDictionary<string, IStreamDeckSurface> Surfaces => _surfaces;

    public IStreamDeckSurface? FindBySerial(string serial) =>
        _surfaces.Values.FirstOrDefault(s => string.Equals(s.Serial, serial, StringComparison.OrdinalIgnoreCase));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[streamdeck-conn] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        DisconnectAll();
    }

    /// <summary>One reconcile + input-pump cycle. Public so tests can step it deterministically.</summary>
    public void Tick()
    {
        if (!_gate.IsEnabled("streamdeck"))
        {
            DisconnectAll();
            return;
        }

        RegisterSimulatedIfNeeded();
        ReconcileHidSurfaces();
        PumpInput();
    }

    private void RegisterSimulatedIfNeeded()
    {
        if (_simulated is null || _surfaces.ContainsKey(SimulatedKey))
        {
            return;
        }
        _surfaces[SimulatedKey] = _simulated;
        _lastKeyStates[SimulatedKey] = new bool[_simulated.Model.KeyCount];
        ServiceLog.Info($"[streamdeck] simulated deck available ({_simulated.Model.Name}, serial={_simulated.Serial})");
    }

    private void ReconcileHidSurfaces()
    {
        var seenPaths = new HashSet<string>();

        if (_presence.UsbPresent(StreamDeckModels.VendorId))
        {
            foreach (var model in StreamDeckModels.All)
            {
                if (model.Protocol != StreamDeckProtocolGeneration.Gen1)
                {
                    continue;
                }
                foreach (var info in _hid.Find(StreamDeckModels.VendorId, model.ProductId))
                {
                    seenPaths.Add(info.Path);
                    if (_surfaces.TryGetValue(info.Path, out var existing) && existing.IsConnected)
                    {
                        continue;
                    }

                    var surface = new HidStreamDeckSurface(_hid, model);
                    if (!surface.Connect(info))
                    {
                        continue;
                    }
                    _surfaces[info.Path] = surface;
                    _lastKeyStates[info.Path] = new bool[model.KeyCount];
                    ServiceLog.Info($"[streamdeck] connected {model.Name} (serial={surface.Serial})");
                }
            }
        }

        foreach (var key in _surfaces.Keys.ToList())
        {
            if (key == SimulatedKey)
            {
                continue;
            }
            if (seenPaths.Contains(key) && _surfaces[key].IsConnected)
            {
                continue;
            }
            ServiceLog.Info($"[streamdeck] disconnected (serial={_surfaces[key].Serial})");
            _surfaces[key].Dispose();
            _surfaces.Remove(key);
            _lastKeyStates.Remove(key);
        }
    }

    private void PumpInput()
    {
        foreach (var (key, surface) in _surfaces)
        {
            if (!surface.IsConnected)
            {
                continue;
            }
            var states = surface.ReadInput(InputPollTimeoutMs);
            if (states is null)
            {
                continue;
            }
            if (!_lastKeyStates.TryGetValue(key, out var last) || last.Length != states.Length)
            {
                last = new bool[states.Length];
                _lastKeyStates[key] = last;
            }
            for (var i = 0; i < states.Length; i++)
            {
                if (states[i] == last[i])
                {
                    continue;
                }
                ServiceLog.Info($"[streamdeck] key {(states[i] ? "down" : "up")} serial={surface.Serial} index={i}");
            }
            states.CopyTo(last, 0);
        }
    }

    private void DisconnectAll()
    {
        foreach (var (key, surface) in _surfaces)
        {
            if (key == SimulatedKey)
            {
                continue;
            }
            surface.Dispose();
        }
        _surfaces.Clear();
        _lastKeyStates.Clear();
    }
}
