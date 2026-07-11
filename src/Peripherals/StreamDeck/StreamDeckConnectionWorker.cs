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
/// keyed by HID path. Reconcile (hot-plug scan + image pushes) runs on a
/// 1-second tick; button input does not. Each real HID surface gets its own
/// <see cref="StreamDeckInputReader"/> - a dedicated thread keeping a blocking
/// interrupt-IN read continuously pending on its own HID handle, started when
/// the surface connects and stopped when it disconnects - so a press between
/// tick windows is never missed and the read never contends with this
/// worker's image/brightness writes. The dev-tools-gated shared simulated
/// deck has no HID handle to read; its synthetic presses are still polled on
/// the tick (see PumpSimulatedInput).
///
/// Also owns per-deck folder navigation and current page (both keyed by
/// serial, so they survive a replug on a different USB port) and dispatches
/// a resolved key's action to <see cref="IDeckActionExecutor"/> off the read
/// thread. A "page" slot is intercepted here rather than reaching the
/// executor: it changes the tracked page instead of dispatching. Implements
/// <see cref="IDeckSurfaceControl"/> so the executor can push a live
/// brightness change or a sleep blank to this deck's own surface.
///
/// Opens every model in <see cref="StreamDeckModels.All"/>, gen1 and gen2
/// alike; only the Mini is bench-verified (StreamDeckModel.Verified).
/// </summary>
public sealed class StreamDeckConnectionWorker : BackgroundService, IDeckSurfaceControl
{
    private const int TickMs = 1000;

    internal const string SimulatedKey = "sim";

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly IConfigStore _store;
    private readonly IDeckActionExecutor _executor;
    private readonly StreamDeckImageCache _imageCache;
    private readonly MultiplexHub _hub;

    /// <summary>
    /// The dev-tools bench simulated deck, if any. Mutable (not just
    /// ctor-assigned): SetSimulatedModel/ClearSimulatedModel swap it at
    /// runtime, guarded by _lock like every other surface mutation.
    /// </summary>
    private SimulatedStreamDeckSurface? _simulated;

    /// <summary>
    /// Guards _surfaces/_lastKeyStates/_folderPathsBySerial/_lastInputAt/_asleep
    /// against the tick thread, an HTTP route (GET /streamdeck/decks,
    /// FindBySerial, RefreshView, IsAsleep), and each connected surface's own
    /// StreamDeckInputReader background thread (via OnInputReport) all reading
    /// or writing the same dictionaries. Reentrant per thread (a plain object
    /// lock), so Tick's internals can call PushCurrentView while already
    /// holding it without deadlocking.
    /// </summary>
    private readonly object _lock = new();

    private readonly Dictionary<string, IStreamDeckSurface> _surfaces = new();
    private readonly Dictionary<string, bool[]> _lastKeyStates = new();
    private readonly Dictionary<string, List<int>> _folderPathsBySerial = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-deck (keyed by serial) current page index, 0-based; default 0 when absent.</summary>
    private readonly Dictionary<string, int> _currentPageBySerial = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-deck (keyed by serial) timestamp of the last key input, real or simulated. Seeded on connect; drives ApplySleepAfterIdle.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastInputAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-deck (keyed by serial) sleep-after state: true once blanked by ApplySleepAfterIdle, cleared by the next key down.</summary>
    private readonly Dictionary<string, bool> _asleep = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One dedicated input reader per connected real HID surface, keyed the same as _surfaces. Never holds an entry for SimulatedKey.</summary>
    private readonly Dictionary<string, StreamDeckInputReader> _inputReaders = new();

    private readonly TimeProvider _clock;

    public StreamDeckConnectionWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        DeviceControlGate gate,
        IConfigStore store,
        IDeckActionExecutor executor,
        StreamDeckImageCache imageCache,
        MultiplexHub hub,
        SimulatedStreamDeckSurface? simulated = null,
        TimeProvider? clock = null)
    {
        _hid = hid;
        _presence = presence;
        _gate = gate;
        _store = store;
        _executor = executor;
        _imageCache = imageCache;
        _hub = hub;
        _simulated = simulated;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A snapshot of every currently tracked surface, keyed by HID path (or "sim" for the simulated deck).</summary>
    public IReadOnlyDictionary<string, IStreamDeckSurface> Surfaces
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, IStreamDeckSurface>(_surfaces);
            }
        }
    }

    public IStreamDeckSurface? FindBySerial(string serial)
    {
        lock (_lock)
        {
            return FindBySerialLocked(serial);
        }
    }

    private IStreamDeckSurface? FindBySerialLocked(string serial) =>
        _surfaces.Values.FirstOrDefault(s => string.Equals(s.Serial, serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if ApplySleepAfterIdle has blanked this deck and no key input has restored it yet.</summary>
    public bool IsAsleep(string serial)
    {
        lock (_lock)
        {
            return _asleep.TryGetValue(serial, out var asleep) && asleep;
        }
    }

    /// <summary>Current folder path for a deck (empty at root). Test/diagnostic accessor.</summary>
    public IReadOnlyList<int> GetFolderPath(string serial)
    {
        lock (_lock)
        {
            return _folderPathsBySerial.TryGetValue(serial, out var path) ? path.ToList() : Array.Empty<int>();
        }
    }

    /// <summary>Current page index for a deck (0-based, default 0). Test/diagnostic accessor.</summary>
    public int GetCurrentPage(string serial)
    {
        lock (_lock)
        {
            return GetCurrentPageLocked(serial);
        }
    }

    private int GetCurrentPageLocked(string serial) =>
        _currentPageBySerial.TryGetValue(serial, out var page) ? page : 0;

    /// <summary>
    /// Applies a desktop-editor navigation (page + folder path) to the deck's
    /// tracked state and re-renders the physical surface, so navigating in the
    /// editor mirrors onto the hardware. Does NOT broadcast a nav frame - only a
    /// real key press does (physical -> desktop), so the desktop's own POST
    /// never echoes back to loop the two directions.
    /// </summary>
    public bool SetNav(string serial, int page, IReadOnlyList<int> folderPath)
    {
        lock (_lock)
        {
            if (!_store.Load().StreamDeck.Decks.ContainsKey(serial))
            {
                return false;
            }
            var config = LoadConfig(serial);
            var pageCount = Math.Max(config.Pages.Count, 1);
            _currentPageBySerial[serial] = Math.Clamp(page, 0, pageCount - 1);
            _folderPathsBySerial[serial] = new List<int>(folderPath);
            var surface = FindBySerialLocked(serial);
            if (surface is not null)
            {
                WakeIfAsleep(surface);
                PushCurrentView(surface);
            }
            return true;
        }
    }

    /// <inheritdoc />
    public void SetBrightness(string serial, int percent)
    {
        lock (_lock)
        {
            FindBySerialLocked(serial)?.SetBrightness(percent);
        }
    }

    /// <inheritdoc />
    public void PutAsleep(string serial)
    {
        lock (_lock)
        {
            var surface = FindBySerialLocked(serial);
            if (surface is null)
            {
                return;
            }
            surface.SetBrightness(0);
            _asleep[surface.Serial] = true;
        }
    }

    /// <summary>
    /// The most recently fire-and-forget dispatched key action, if any. Test
    /// seam only: HandleKeyDown intentionally does not await this (a slow or
    /// throwing action must never stall the input read loop for other decks).
    /// </summary>
    internal Task? LastDispatchTask { get; private set; }

    /// <summary>Re-pushes cached key images for a deck's current folder view. Call after a config or image-ref mutation.</summary>
    public void RefreshView(string serial)
    {
        lock (_lock)
        {
            var surface = FindBySerialLocked(serial);
            if (surface is not null)
            {
                PushCurrentView(surface);
            }
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

    /// <summary>
    /// One reconcile cycle: hot-plug scan, image pushes, and (simulated
    /// surface only) an input poll. Public so tests can step it
    /// deterministically. Real HID input arrives via each surface's dedicated
    /// StreamDeckInputReader, not this tick.
    /// </summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (!_gate.IsEnabled("streamdeck"))
            {
                DisconnectAll();
                return;
            }

            RegisterSimulatedIfNeeded();
            ReconcileHidSurfaces();
            PumpSimulatedInput();
            ApplySleepAfterIdle();
        }
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

    /// <summary>
    /// Dev-tools bench hook: swaps the simulated deck to the given model,
    /// tearing down any previous simulated surface first (same cleanup path
    /// as a real unplug). Returns false without side effects for an
    /// unrecognized product id. Callable from an HTTP route thread
    /// concurrently with the tick thread; takes _lock itself. Registers the
    /// new surface immediately, so it appears in Surfaces/GET
    /// /streamdeck/decks without waiting for the next tick. Only the DI
    /// wiring and the routes that call this are dev-gated - like
    /// SimulatedStreamDeckSurface itself, this method compiles and is tested
    /// unconditionally.
    /// </summary>
    public bool SetSimulatedModel(int productId)
    {
        var model = StreamDeckModels.ByProductId(productId);
        if (model is null)
        {
            return false;
        }
        lock (_lock)
        {
            RemoveSimulatedLocked();
            _simulated = new SimulatedStreamDeckSurface(model, $"sim-{model.ProductId:x4}");
            RegisterSimulatedIfNeeded();
        }
        return true;
    }

    /// <summary>Dev-tools bench hook: removes the simulated deck, if any. Same cleanup path as a real unplug.</summary>
    public void ClearSimulatedModel()
    {
        lock (_lock)
        {
            RemoveSimulatedLocked();
            _simulated = null;
        }
    }

    /// <summary>
    /// Tears down the currently tracked simulated surface, if any: removes it
    /// from _surfaces and clears its serial-keyed nav/idle state, mirroring
    /// ReconcileHidSurfaces' disconnect path. Caller must hold _lock.
    /// </summary>
    private void RemoveSimulatedLocked()
    {
        if (!_surfaces.TryGetValue(SimulatedKey, out var existing))
        {
            return;
        }
        existing.Dispose();
        _surfaces.Remove(SimulatedKey);
        _lastKeyStates.Remove(SimulatedKey);
        _folderPathsBySerial.Remove(existing.Serial);
        _lastInputAt.Remove(existing.Serial);
        _asleep.Remove(existing.Serial);
        _currentPageBySerial.Remove(existing.Serial);
        BroadcastDecksChanged(existing.Serial);
    }

    /// <summary>Test seam: true if folder-nav/last-input/sleep/page state is still tracked for this serial. Asserts SetSimulatedModel/ClearSimulatedModel do not leak entries across a swap.</summary>
    internal bool HasSerialState(string serial)
    {
        lock (_lock)
        {
            return _folderPathsBySerial.ContainsKey(serial) || _lastInputAt.ContainsKey(serial) || _asleep.ContainsKey(serial) || _currentPageBySerial.ContainsKey(serial);
        }
    }

    private void ReconcileHidSurfaces()
    {
        var seenPaths = new HashSet<string>();

        if (_presence.UsbPresent(StreamDeckModels.VendorId))
        {
            foreach (var model in StreamDeckModels.All)
            {
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
                    StartInputReader(info.Path, surface);
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
            StopInputReader(key);
            _surfaces[key].Dispose();
            _surfaces.Remove(key);
            _lastKeyStates.Remove(key);
            _folderPathsBySerial.Remove(serial);
            _lastInputAt.Remove(serial);
            _asleep.Remove(serial);
            _currentPageBySerial.Remove(serial);
            BroadcastDecksChanged(serial);
        }
    }

    /// <summary>Starts the dedicated blocking-read thread for a newly connected real HID surface. No-op if one is already running for this key.</summary>
    private void StartInputReader(string key, IStreamDeckSurface surface)
    {
        if (_inputReaders.ContainsKey(key))
        {
            return;
        }
        _inputReaders[key] = new StreamDeckInputReader(_hid, key, surface.Model, states => OnInputReport(key, states));
    }

    /// <summary>Signals a surface's dedicated reader thread to stop. Non-blocking; see StreamDeckInputReader.Dispose.</summary>
    private void StopInputReader(string key)
    {
        if (_inputReaders.Remove(key, out var reader))
        {
            reader.Dispose();
        }
    }

    /// <summary>Applies persisted brightness, resets folder nav to root, and pushes the root view's cached images.</summary>
    private void OnSurfaceConnected(IStreamDeckSurface surface)
    {
        ApplyPersistedBrightness(surface);

        _store.Update(s =>
        {
            if (!s.StreamDeck.Decks.TryGetValue(surface.Serial, out var persisted))
            {
                persisted = new PhysicalDeckSettings();
                s.StreamDeck.Decks[surface.Serial] = persisted;
            }
            persisted.ProductId = surface.Model.ProductId;
        });

        _lastInputAt[surface.Serial] = _clock.GetUtcNow();
        _asleep[surface.Serial] = false;
        _folderPathsBySerial[surface.Serial] = new List<int>();
        _currentPageBySerial[surface.Serial] = 0;
        PushCurrentView(surface);
        BroadcastDecksChanged(surface.Serial);
    }

    /// <summary>Pushes the persisted (or default) brightness to a surface. Shared by connect and sleep-after wake.</summary>
    private void ApplyPersistedBrightness(IStreamDeckSurface surface)
    {
        var settings = _store.Load().StreamDeck;
        var brightness = settings.Decks.TryGetValue(surface.Serial, out var deck)
            ? deck.Brightness
            : PhysicalDeckSettings.DefaultBrightness;
        surface.SetBrightness(brightness);
    }

    /// <summary>
    /// Drains every queued synthetic press transition for the shared
    /// simulated deck. Only SimulatedStreamDeckSurface goes through the
    /// tick: it has no HID handle for a dedicated reader, and its
    /// ReadInput never blocks. Draining the whole queue (not one poll)
    /// ensures a press and release within one tick both dispatch, instead
    /// of the tick sampling only the latest state and dropping a
    /// sub-tick transition.
    /// </summary>
    private void PumpSimulatedInput()
    {
        if (!_surfaces.TryGetValue(SimulatedKey, out var surface) || !surface.IsConnected)
        {
            return;
        }
        bool[]? states;
        while ((states = surface.ReadInput(0)) is not null)
        {
            ProcessKeyStates(SimulatedKey, surface, states);
        }
    }

    /// <summary>
    /// Blanks the display of any connected deck whose SleepAfterSeconds has
    /// elapsed with no key input since _lastInputAt. Runs once per tick, so
    /// idle detection resolves within one TickMs of the configured threshold;
    /// a deck already marked asleep is skipped so it is blanked only once per
    /// idle period. HandleKeyDown clears the flag and restores brightness on
    /// the next key press.
    /// </summary>
    private void ApplySleepAfterIdle()
    {
        var settings = _store.Load().StreamDeck;
        var now = _clock.GetUtcNow();
        foreach (var surface in _surfaces.Values)
        {
            if (!surface.IsConnected)
            {
                continue;
            }
            if (!settings.Decks.TryGetValue(surface.Serial, out var deck) || deck.SleepAfterSeconds <= 0)
            {
                continue;
            }
            if (_asleep.TryGetValue(surface.Serial, out var alreadyAsleep) && alreadyAsleep)
            {
                continue;
            }
            if (!_lastInputAt.TryGetValue(surface.Serial, out var lastInput) ||
                now - lastInput < TimeSpan.FromSeconds(deck.SleepAfterSeconds))
            {
                continue;
            }
            surface.SetBrightness(0);
            _asleep[surface.Serial] = true;
            ServiceLog.Info($"[streamdeck] deck asleep after {deck.SleepAfterSeconds}s idle (serial={surface.Serial})");
        }
    }

    /// <summary>Restores persisted brightness and clears the sleep-after flag if this deck was blanked. No-op otherwise.</summary>
    private void WakeIfAsleep(IStreamDeckSurface surface)
    {
        if (!_asleep.TryGetValue(surface.Serial, out var asleep) || !asleep)
        {
            return;
        }
        _asleep[surface.Serial] = false;
        ApplyPersistedBrightness(surface);
        ServiceLog.Info($"[streamdeck] deck woken by key input (serial={surface.Serial})");
    }

    /// <summary>
    /// Diffs a decoded key-state snapshot against the last known state for
    /// this surface and dispatches any newly-pressed key. Caller must hold
    /// _lock.
    /// </summary>
    private void ProcessKeyStates(string key, IStreamDeckSurface surface, bool[] states)
    {
        _lastInputAt[surface.Serial] = _clock.GetUtcNow();
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

    /// <summary>
    /// Callback for a real HID surface's dedicated StreamDeckInputReader,
    /// invoked from that surface's own read thread. Acquires _lock itself
    /// (unlike the tick-driven helpers, which assume it is already held) and
    /// never runs while a blocking wire read is pending.
    /// </summary>
    private void OnInputReport(string key, bool[] states)
    {
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(key, out var surface))
            {
                return;
            }
            ProcessKeyStates(key, surface, states);
        }
    }

    private void HandleKeyDown(IStreamDeckSurface surface, int physicalIndex)
    {
        WakeIfAsleep(surface);

        var serial = surface.Serial;
        var page = GetCurrentPageLocked(serial);
        if (!_folderPathsBySerial.TryGetValue(serial, out var folderPath))
        {
            folderPath = new List<int>();
            _folderPathsBySerial[serial] = folderPath;
        }
        var inFolder = folderPath.Count > 0;

        if (inFolder && physicalIndex == 0)
        {
            var popped = new List<int>(folderPath);
            popped.RemoveAt(popped.Count - 1);
            _folderPathsBySerial[serial] = popped;
            PushCurrentView(surface);
            BroadcastNav(serial, page, popped);
            return;
        }

        var slotIndex = inFolder ? physicalIndex - 1 : physicalIndex;
        var config = LoadConfig(serial);
        var view = DeckConfigNavigation.ResolveView(config, page, folderPath);
        if (view is null || slotIndex < 0 || slotIndex >= view.Count)
        {
            return;
        }

        HandleSlotAction(serial, config, page, folderPath, slotIndex, view[slotIndex]);
    }

    /// <summary>
    /// Resolves and simulates a press at a config slot path (test-press):
    /// same nav-or-dispatch decision as a real key press (HandleSlotAction),
    /// so a "page" action changes GetCurrentPage and a folder slot pushes
    /// folder nav exactly as pressing the physical key would. Uses the
    /// deck's current tracked page but the slot path's own folder indices,
    /// not the deck's live tracked folder - a test-press can target any
    /// configured slot regardless of what the physical keys currently show.
    /// Works even when the deck has no live surface (image push and the
    /// sleep wake are skipped then; nav/dispatch state still updates).
    /// Returns false when the path does not resolve to an action or folder
    /// slot.
    /// </summary>
    public bool SimulatePress(string serial, IReadOnlyList<int> indices, DeckConfig config)
    {
        lock (_lock)
        {
            if (indices.Count == 0)
            {
                return false;
            }
            var page = GetCurrentPageLocked(serial);
            var slot = DeckConfigNavigation.ResolveSlot(config, page, indices);
            if (slot is null || (slot.Action is null && slot.Folder is null))
            {
                return false;
            }
            var liveSurface = FindBySerialLocked(serial);
            if (liveSurface is not null)
            {
                WakeIfAsleep(liveSurface);
            }
            var folderPath = new List<int>(indices);
            var slotIndex = folderPath[^1];
            folderPath.RemoveAt(folderPath.Count - 1);
            HandleSlotAction(serial, config, page, folderPath, slotIndex, slot);
            return true;
        }
    }

    /// <summary>
    /// Given a slot already resolved at (page, folderPath, slotIndex),
    /// applies the same nav-or-dispatch decision a real key press makes: a
    /// folder slot pushes folder nav, a "page" action changes the tracked
    /// page, "pageIndicator" is display-only, and any other action
    /// dispatches to the executor off this thread (fire-and-forget, see
    /// LastDispatchTask). The live view is only re-pushed to hardware when a
    /// surface is actually connected for this serial. Caller must hold
    /// _lock. Shared by HandleKeyDown and SimulatePress.
    /// </summary>
    private void HandleSlotAction(string serial, DeckConfig config, int page, List<int> folderPath, int slotIndex, DeckSlot slot)
    {
        if (slot.Folder is not null)
        {
            var pushed = new List<int>(folderPath) { slotIndex };
            _folderPathsBySerial[serial] = pushed;
            var pushSurface = FindBySerialLocked(serial);
            if (pushSurface is not null)
            {
                PushCurrentView(pushSurface);
            }
            BroadcastNav(serial, page, pushed);
            return;
        }
        if (slot.Action is null)
        {
            return;
        }
        if (slot.Action.Type == "page")
        {
            HandlePageAction(serial, config, slot.Action);
            return;
        }
        if (slot.Action.Type == "pageIndicator")
        {
            return;
        }

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

    /// <summary>
    /// Applies a "page" slot's next/prev/goto op, clamped to the config's
    /// page range. Always resets folder nav to root and re-pushes the view -
    /// a page-nav press is always meant to land on that page's root, even
    /// when clamping leaves the page index unchanged (e.g. "prev" at page 0).
    /// </summary>
    private void HandlePageAction(string serial, DeckConfig config, DeckAction action)
    {
        var pageCount = Math.Max(config.Pages.Count, 1);
        var current = GetCurrentPageLocked(serial);
        var next = action.Op switch
        {
            "next" => current + 1,
            "prev" => current - 1,
            "goto" => action.Target ?? current,
            _ => current,
        };
        next = Math.Clamp(next, 0, pageCount - 1);
        _currentPageBySerial[serial] = next;
        _folderPathsBySerial[serial] = new List<int>();
        var surface = FindBySerialLocked(serial);
        if (surface is not null)
        {
            PushCurrentView(surface);
        }
        BroadcastNav(serial, next, new List<int>());
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
        var page = ClampCurrentPageLocked(surface.Serial, config);

        var view = DeckConfigNavigation.ResolveView(config, page, folderPath);
        if (view is null)
        {
            folderPath = new List<int>();
            _folderPathsBySerial[surface.Serial] = folderPath;
            view = DeckConfigNavigation.ResolveView(config, page, folderPath) ?? new List<DeckSlot>();
        }

        var inFolder = folderPath.Count > 0;
        for (var key = 0; key < surface.Model.KeyCount; key++)
        {
            if (inFolder && key == 0)
            {
                PushBackKey(surface, deck);
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

    /// <summary>Reserved image-ref key the web uploads once per deck via PUT .../images/back/0 (not a real DeckSlot).</summary>
    private const string BackSlotPath = "back";

    private void PushBackKey(IStreamDeckSurface surface, PhysicalDeckSettings? deck)
    {
        var hash = deck is not null && deck.ImageRefs.TryGetValue($"{BackSlotPath}/0", out var h) ? h : null;
        var bytes = hash is not null ? _imageCache.Load(surface.Serial, hash) : null;
        if (bytes is not null)
        {
            surface.SetKeyImage(0, bytes);
        }
        else
        {
            surface.ClearKey(0);
        }
    }

    private DeckConfig LoadConfig(string serial)
    {
        var settings = _store.Load().StreamDeck;
        return settings.Decks.TryGetValue(serial, out var deck) ? deck.Deck : new DeckConfig();
    }

    /// <summary>
    /// Clamps a deck's tracked current page to the config's actual page
    /// range, updating the tracked value when a config edit (or a config
    /// with no persisted deck yet) has shrunk it out of range. Caller must
    /// hold _lock.
    /// </summary>
    private int ClampCurrentPageLocked(string serial, DeckConfig config)
    {
        var pageCount = Math.Max(config.Pages.Count, 1);
        var current = GetCurrentPageLocked(serial);
        var clamped = Math.Clamp(current, 0, pageCount - 1);
        if (clamped != current)
        {
            _currentPageBySerial[serial] = clamped;
        }
        return clamped;
    }

    private static string BuildLatchKey(string serial, IReadOnlyList<int> folderPath, int slotIndex) =>
        $"{serial}:{string.Join('.', folderPath)}:{slotIndex}";

    private void BroadcastDecksChanged(string? serial) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });

    private void BroadcastNav(string serial, int page, List<int> folderPath) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "nav", Serial = serial, Page = page, FolderPath = folderPath });

    private void BroadcastPress(string serial, List<int> folderPath, int keyIndex) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame { Kind = "press", Serial = serial, FolderPath = folderPath, KeyIndex = keyIndex });

    private void DisconnectAll()
    {
        lock (_lock)
        {
            var hadSurfaces = _surfaces.Count > 0;
            foreach (var (key, surface) in _surfaces)
            {
                if (key == SimulatedKey)
                {
                    continue;
                }
                StopInputReader(key);
                surface.Dispose();
            }
            _surfaces.Clear();
            _lastKeyStates.Clear();
            _folderPathsBySerial.Clear();
            _lastInputAt.Clear();
            _asleep.Clear();
            _currentPageBySerial.Clear();
            if (hadSurfaces)
            {
                BroadcastDecksChanged(null);
            }
        }
    }

    /// <summary>Stops every input reader thread and disposes tracked surfaces, so neither a container disposal nor a test-scoped worker leaks a background thread.</summary>
    public override void Dispose()
    {
        DisconnectAll();
        base.Dispose();
    }
}
