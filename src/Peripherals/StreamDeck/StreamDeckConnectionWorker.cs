using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Discovers and owns every connected Stream Deck's <see cref="IStreamDeckSurface"/>,
/// keyed by HID path. Surface-agnostic: real HID decks and (dev-tools-gated)
/// the shared simulated deck are tracked in the same dictionary and pumped
/// through the same input loop, so the worker never branches on transport.
///
/// Also owns per-deck folder navigation (keyed by serial, so it survives a
/// replug on a different USB port) and dispatches a resolved key's action to
/// <see cref="IDeckActionExecutor"/> off the tick thread.
///
/// Phase 0 only opens Gen1-protocol models (the bench-verified Mini and its
/// BMP siblings); Gen2 detection/connection lands in Phase 3.
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
    private readonly IConfigStore _store;
    private readonly IDeckActionExecutor _executor;
    private readonly StreamDeckImageCache _imageCache;
    private readonly MultiplexHub _hub;
    private readonly SimulatedStreamDeckSurface? _simulated;
    private readonly Dictionary<string, IStreamDeckSurface> _surfaces = new();
    private readonly Dictionary<string, bool[]> _lastKeyStates = new();
    private readonly Dictionary<string, List<int>> _folderPathsBySerial = new(StringComparer.OrdinalIgnoreCase);

    public StreamDeckConnectionWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        DeviceControlGate gate,
        IConfigStore store,
        IDeckActionExecutor executor,
        StreamDeckImageCache imageCache,
        MultiplexHub hub,
        SimulatedStreamDeckSurface? simulated = null)
    {
        _hid = hid;
        _presence = presence;
        _gate = gate;
        _store = store;
        _executor = executor;
        _imageCache = imageCache;
        _hub = hub;
        _simulated = simulated;
    }

    /// <summary>Every currently tracked surface, keyed by HID path (or "sim" for the simulated deck).</summary>
    public IReadOnlyDictionary<string, IStreamDeckSurface> Surfaces => _surfaces;

    public IStreamDeckSurface? FindBySerial(string serial) =>
        _surfaces.Values.FirstOrDefault(s => string.Equals(s.Serial, serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>Current folder path for a deck (empty at root). Test/diagnostic accessor.</summary>
    public IReadOnlyList<int> GetFolderPath(string serial) =>
        _folderPathsBySerial.TryGetValue(serial, out var path) ? path : Array.Empty<int>();

    /// <summary>
    /// The most recently fire-and-forget dispatched key action, if any. Test
    /// seam only: HandleKeyDown intentionally does not await this (a slow or
    /// throwing action must never stall PumpInput for other decks).
    /// </summary>
    internal Task? LastDispatchTask { get; private set; }

    /// <summary>Re-pushes cached key images for a deck's current folder view. Call after a config or image-ref mutation.</summary>
    public void RefreshView(string serial)
    {
        var surface = FindBySerial(serial);
        if (surface is not null)
        {
            PushCurrentView(surface);
        }
    }

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
        OnSurfaceConnected(_simulated);
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
                    OnSurfaceConnected(surface);
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
            var serial = _surfaces[key].Serial;
            ServiceLog.Info($"[streamdeck] disconnected (serial={serial})");
            _surfaces[key].Dispose();
            _surfaces.Remove(key);
            _lastKeyStates.Remove(key);
            _folderPathsBySerial.Remove(serial);
            BroadcastDecksChanged(serial);
        }
    }

    /// <summary>Applies persisted brightness, resets folder nav to root, and pushes the root view's cached images.</summary>
    private void OnSurfaceConnected(IStreamDeckSurface surface)
    {
        var settings = _store.Load().StreamDeck;
        var brightness = settings.Decks.TryGetValue(surface.Serial, out var deck)
            ? deck.Brightness
            : PhysicalDeckSettings.DefaultBrightness;
        surface.SetBrightness(brightness);
        _folderPathsBySerial[surface.Serial] = new List<int>();
        PushCurrentView(surface);
        BroadcastDecksChanged(surface.Serial);
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
                if (states[i])
                {
                    HandleKeyDown(surface, i);
                }
            }
            states.CopyTo(last, 0);
        }
    }

    private void HandleKeyDown(IStreamDeckSurface surface, int physicalIndex)
    {
        if (!_folderPathsBySerial.TryGetValue(surface.Serial, out var folderPath))
        {
            folderPath = new List<int>();
            _folderPathsBySerial[surface.Serial] = folderPath;
        }
        var inFolder = folderPath.Count > 0;

        if (inFolder && physicalIndex == 0)
        {
            var popped = new List<int>(folderPath);
            popped.RemoveAt(popped.Count - 1);
            _folderPathsBySerial[surface.Serial] = popped;
            PushCurrentView(surface);
            BroadcastNav(surface.Serial, popped);
            return;
        }

        var slotIndex = inFolder ? physicalIndex - 1 : physicalIndex;
        var config = LoadConfig(surface.Serial);
        var view = DeckConfigNavigation.ResolveView(config, folderPath);
        if (view is null || slotIndex < 0 || slotIndex >= view.Count)
        {
            return;
        }

        var slot = view[slotIndex];
        if (slot.Folder is not null)
        {
            var pushed = new List<int>(folderPath) { slotIndex };
            _folderPathsBySerial[surface.Serial] = pushed;
            PushCurrentView(surface);
            BroadcastNav(surface.Serial, pushed);
            return;
        }
        if (slot.Action is null)
        {
            return;
        }

        var serial = surface.Serial;
        var action = slot.Action;
        var latchKey = BuildLatchKey(serial, folderPath, slotIndex);
        var folderPathSnapshot = new List<int>(folderPath);
        LastDispatchTask = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteAsync(action, serial, slotIndex, latchKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[streamdeck] key dispatch crashed serial={serial} key={slotIndex}: {ex.Message}");
            }
        });
        BroadcastPress(serial, folderPathSnapshot, slotIndex);
    }

    private void PushCurrentView(IStreamDeckSurface surface)
    {
        if (!_folderPathsBySerial.TryGetValue(surface.Serial, out var folderPath))
        {
            folderPath = new List<int>();
        }
        var settings = _store.Load().StreamDeck;
        settings.Decks.TryGetValue(surface.Serial, out var deck);
        var config = deck?.Deck ?? new DeckConfig();

        var view = DeckConfigNavigation.ResolveView(config, folderPath);
        if (view is null)
        {
            folderPath = new List<int>();
            _folderPathsBySerial[surface.Serial] = folderPath;
            view = DeckConfigNavigation.ResolveView(config, folderPath) ?? new List<DeckSlot>();
        }

        var inFolder = folderPath.Count > 0;
        for (var key = 0; key < surface.Model.KeyCount; key++)
        {
            if (inFolder && key == 0)
            {
                surface.ClearKey(key);
                continue;
            }
            var slotIndex = inFolder ? key - 1 : key;
            var slot = slotIndex >= 0 && slotIndex < view.Count ? view[slotIndex] : null;
            if (slot is null)
            {
                surface.ClearKey(key);
                continue;
            }

            var latchKey = BuildLatchKey(surface.Serial, folderPath, slotIndex);
            var state = slot.Action?.Type == "toggle" && _executor.IsToggleOn(slot.Action.State, latchKey) ? "1" : "0";
            var slotPath = DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex);
            var hash = deck is not null && deck.ImageRefs.TryGetValue($"{slotPath}/{state}", out var h) ? h : null;
            var bytes = hash is not null ? _imageCache.Load(surface.Serial, hash) : null;
            if (bytes is not null)
            {
                surface.SetKeyImage(key, bytes);
            }
            else
            {
                surface.ClearKey(key);
            }
        }
    }

    private DeckConfig LoadConfig(string serial)
    {
        var settings = _store.Load().StreamDeck;
        return settings.Decks.TryGetValue(serial, out var deck) ? deck.Deck : new DeckConfig();
    }

    private static string BuildLatchKey(string serial, IReadOnlyList<int> folderPath, int slotIndex) =>
        $"{serial}:{string.Join('.', folderPath)}:{slotIndex}";

    private void BroadcastDecksChanged(string? serial) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });

    private void BroadcastNav(string serial, List<int> folderPath) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "nav", Serial = serial, FolderPath = folderPath });

    private void BroadcastPress(string serial, List<int> folderPath, int keyIndex) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "press", Serial = serial, FolderPath = folderPath, KeyIndex = keyIndex });

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
        _folderPathsBySerial.Clear();
        BroadcastDecksChanged(null);
    }
}
