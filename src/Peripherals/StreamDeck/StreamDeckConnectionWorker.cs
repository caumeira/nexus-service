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
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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

    /// <summary>Elapsed hold on a blank key before the open-editor intent fires.</summary>
    private const int HoldToEditMs = 700;
    /// <summary>Fill-ring frame interval while a blank key is held.</summary>
    private const int HoldFrameMs = 50;
    /// <summary>Distinct fill-ring frames the hold animation quantizes to (0..HoldRingSteps), bounding the per-model render cache.</summary>
    private const int HoldRingSteps = 24;
    /// <summary>How long a fired hold-to-edit intent stays served by GET /streamdeck/pending-edit before it ages out (covers the app cold-launch window).</summary>
    private static readonly TimeSpan PendingEditTtl = TimeSpan.FromSeconds(20);

    internal const string SimulatedKey = "sim";

    /// <summary>Round-robin cap on rendered+pushed monitoring keys per tick; sampling (history append) is unbounded.</summary>
    private const int MonitoringPushCapPerTick = 4;
    private const int MonitoringHistoryLength = 40;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly IConfigStore _store;
    private readonly IDeckActionExecutor _executor;
    private readonly StreamDeckImageCache _imageCache;
    private readonly MultiplexHub _hub;
    private readonly ISensorProvider _sensors;

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

    /// <summary>Per-deck (keyed by serial) physical key indices HandlePressVisual marked held (key-down seen, matching key-up not yet seen). Gates HandleKeyUp's restore/refresh and RefreshMonitoringKeys' per-key push skip.</summary>
    private readonly Dictionary<string, HashSet<int>> _heldKeysBySerial = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In-progress blank-key holds, per deck (serial) then per physical key, so
    /// several blank keys held at once each animate and clear independently.
    /// Written on the input thread (StartHoldEdit / key-up) and on the animation
    /// loop (AnimateHolds fill + fire), all under _lock. An empty inner map is
    /// never left behind - its serial entry is removed with it.
    /// </summary>
    private readonly Dictionary<string, Dictionary<int, HoldEditState>> _activeHolds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Lock-free hint so the animation loop skips taking _lock every frame when no hold is in progress; the authoritative check is _activeHolds under _lock.</summary>
    private volatile bool _anyHoldActive;
    /// <summary>Rendered fill-ring wire bytes keyed by "{productId}:{orientation}:{frameIndex}"; a given model/orientation renders each frame at most once.</summary>
    private readonly Dictionary<string, byte[]> _holdFrameCache = new(StringComparer.Ordinal);
    /// <summary>The most recent fired hold-to-edit intent, or null; served (within PendingEditTtl) by GET /streamdeck/pending-edit. Guarded by _lock.</summary>
    private DeckPendingEdit? _pendingEdit;

    private sealed class HoldEditState
    {
        public int Page;
        public List<int> FolderPath = new();
        public int SlotIndex;
        public DateTimeOffset StartedAt;
        public int LastFrameIndex = -1;
    }

    /// <summary>A fired blank-key hold-to-edit intent. Token is the creation epoch ms; the web dedupes the live frame against the boot GET on it.</summary>
    internal readonly record struct DeckPendingEdit(string Serial, int Page, IReadOnlyList<int> FolderPath, int SlotIndex, long Token, DateTimeOffset CreatedAt);

    /// <summary>Per "{serial}:{page}:{slotPath}" monitoring key sample history, oldest first, capped at MonitoringHistoryLength. Page-qualified because BuildSlotPath is not itself unique across a deck's pages.</summary>
    private readonly Dictionary<string, List<float>> _monitoringHistory = new(StringComparer.Ordinal);
    /// <summary>Per "{serial}:{page}:{slotPath}" last-pushed render hash, so an unchanged-looking tile is not re-pushed over HID every tick.</summary>
    private readonly Dictionary<string, uint> _monitoringLastHash = new(StringComparer.Ordinal);
    /// <summary>
    /// Per "{serial}:{page}:{slotPath}" last-pushed wire bytes, alongside
    /// _monitoringLastHash. HandlePressVisual renders a pressed inset from
    /// this on key-down; HandleKeyUp restores it verbatim on release rather
    /// than waiting for the next tick. Cleared everywhere _monitoringLastHash
    /// entries are removed, except ForceMonitoringKeyRefresh: that drops only
    /// the hash (to force a repaint even on an unchanged reading) and keeps
    /// the bytes, so a pressed inset stays renderable for a key pressed again
    /// before the next tick's repaint lands.
    /// </summary>
    private readonly Dictionary<string, byte[]> _monitoringLastPushedBytes = new(StringComparer.Ordinal);
    /// <summary>Cursor into the current tick's visible-monitoring-key list, so a push-capped tick advances fairly across ticks instead of starving keys past the cap.</summary>
    private int _monitoringRoundRobinCursor;

    /// <summary>
    /// Monitoring keys (by historyKey) already sampled and pushed inline by a
    /// PushCurrentView call within the current Tick(), so RefreshMonitoringKeys
    /// later in the same Tick() does not sample them a second time - a second
    /// sample changes a history-length-sensitive style's rendered pixels even
    /// though the reading did not change, forcing a redundant re-push. Cleared
    /// at the start of every Tick(); a PushCurrentView call between ticks (nav,
    /// SetNav, RefreshView) leaves an entry that the next Tick() clears before
    /// RefreshMonitoringKeys runs, so it never suppresses a real periodic sample.
    /// </summary>
    private readonly HashSet<string> _monitoringPaintedThisTick = new(StringComparer.Ordinal);

    private readonly TimeProvider _clock;

    public StreamDeckConnectionWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        DeviceControlGate gate,
        IConfigStore store,
        IDeckActionExecutor executor,
        StreamDeckImageCache imageCache,
        MultiplexHub hub,
        ISensorProvider sensors,
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
        _sensors = sensors;
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

    /// <summary>
    /// True if (page, folderPath) is the deck's currently displayed view.
    /// The image upload route uses this to skip a repaint for a page/folder
    /// the deck is not showing right now - image-refs v2 has the web upload
    /// every page and folder on each edit, not just the one on screen.
    /// </summary>
    public bool IsCurrentView(string serial, int page, IReadOnlyList<int> folderPath)
    {
        lock (_lock)
        {
            if (FindBySerialLocked(serial) is null)
            {
                return false;
            }
            if (GetCurrentPageLocked(serial) != page)
            {
                return false;
            }
            var tracked = _folderPathsBySerial.TryGetValue(serial, out var fp) ? fp : new List<int>();
            return tracked.SequenceEqual(folderPath);
        }
    }

    /// <summary>
    /// True if the deck has a live surface and is currently inside any
    /// folder. The reserved "back" ImageRefs key is page-independent, so the
    /// image upload route uses this instead of IsCurrentView to decide
    /// whether an uploaded back-key image is visible right now.
    /// </summary>
    public bool IsShowingAFolder(string serial)
    {
        lock (_lock)
        {
            if (FindBySerialLocked(serial) is null)
            {
                return false;
            }
            return _folderPathsBySerial.TryGetValue(serial, out var fp) && fp.Count > 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var animation = AnimateLoopAsync(stoppingToken);
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
        await animation.ConfigureAwait(false);
        DisconnectAll();
    }

    /// <summary>
    /// Fast frame clock for in-progress blank-key holds, separate from the
    /// 1-second reconcile Tick so the fill ring animates smoothly. Skips
    /// taking _lock while no hold is active (the _anyHoldActive hint).
    /// </summary>
    private async Task AnimateLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HoldFrameMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (_anyHoldActive) { AnimateHolds(); } }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[streamdeck-conn] hold animation exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
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

            _monitoringPaintedThisTick.Clear();
            RegisterSimulatedIfNeeded();
            ReconcileHidSurfaces();
            PumpSimulatedInput();
            ApplySleepAfterIdle();
            RefreshMonitoringKeys();
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
        _heldKeysBySerial.Remove(existing.Serial);
        RemoveAllHoldsForSerial(existing.Serial);
        RemoveMonitoringStateForSerial(existing.Serial);
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

    /// <summary>Test seam: snapshot of a monitoring key's history ring buffer, or null if no entry exists.</summary>
    internal IReadOnlyList<float>? MonitoringHistoryForTests(string serial, int page, string slotPath)
    {
        lock (_lock)
        {
            return _monitoringHistory.TryGetValue(BuildMonitoringKey(serial, page, slotPath), out var history) ? history.ToList() : null;
        }
    }

    /// <summary>Test seam: true if a last-pushed-hash entry exists for this monitoring key.</summary>
    internal bool HasMonitoringHashForTests(string serial, int page, string slotPath)
    {
        lock (_lock)
        {
            return _monitoringLastHash.ContainsKey(BuildMonitoringKey(serial, page, slotPath));
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
            _heldKeysBySerial.Remove(serial);
            RemoveAllHoldsForSerial(serial);
            RemoveMonitoringStateForSerial(serial);
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
            else
            {
                HandleKeyUp(surface, i);
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
        if (view is null || slotIndex < 0)
        {
            return;
        }

        // A blank (off) key - a key past the configured slots, or an empty slot
        // with no action, folder, or color (IsBlankOffSlot, the same keys that
        // render off) - starts a hold-to-edit instead of the no-op a press on
        // nothing used to be. A key with an action, folder, or decorative color
        // keeps its existing press behavior (so a colored key restores its fill
        // on release rather than being stranded on the ring frame).
        var slot = slotIndex < view.Count ? view[slotIndex] : null;
        if (slot is null || IsBlankOffSlot(slot))
        {
            StartHoldEdit(surface, physicalIndex, page, folderPath, slotIndex);
            return;
        }

        HandleSlotAction(serial, config, page, folderPath, slotIndex, slot);
        HandlePressVisual(surface, physicalIndex, page, folderPath, slotIndex, slot);
    }

    /// <summary>
    /// Restores a physical key's un-pressed image on release, mirroring
    /// HandleKeyDown's resolution. Skipped for a key HandlePressVisual never
    /// marked held: the back key and a folder/page-nav key (their PushCurrentView
    /// call already gave instant visual feedback on the down edge, so there is
    /// nothing pressed to undo), and any key released without a prior down this
    /// worker saw (e.g. across a reconnect mid-hold).
    /// </summary>
    private void HandleKeyUp(IStreamDeckSurface surface, int physicalIndex)
    {
        var serial = surface.Serial;
        // Released before the hold-to-edit fired: drop the fill ring and go
        // back to blank. A hold that already fired is no longer tracked here
        // (AnimateHolds cleared it and blanked the key), so it falls through.
        if (TryEndHoldEdit(serial, physicalIndex))
        {
            surface.ClearKey(physicalIndex);
            return;
        }
        if (!UnmarkKeyHeld(serial, physicalIndex))
        {
            return;
        }

        var page = GetCurrentPageLocked(serial);
        var folderPath = _folderPathsBySerial.TryGetValue(serial, out var fp) ? fp : new List<int>();
        var inFolder = folderPath.Count > 0;
        if (inFolder && physicalIndex == 0)
        {
            return;
        }

        var slotIndex = inFolder ? physicalIndex - 1 : physicalIndex;
        var config = LoadConfig(serial);
        var view = DeckConfigNavigation.ResolveView(config, page, folderPath);
        if (view is null || slotIndex < 0 || slotIndex >= view.Count)
        {
            return;
        }
        var slot = view[slotIndex];

        if (slot.Action?.Type == "monitoring")
        {
            var monitoringKey = BuildMonitoringKey(serial, page, DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex));
            if (_monitoringLastPushedBytes.TryGetValue(monitoringKey, out var originalBytes))
            {
                surface.SetKeyImage(physicalIndex, originalBytes);
            }
            ForceMonitoringKeyRefresh(serial, page, folderPath, slotIndex);
            return;
        }

        var deck = _store.Load().StreamDeck.Decks.TryGetValue(serial, out var d) ? d : null;
        var (bytes, _) = ResolveSlotImage(serial, page, folderPath, slotIndex, slot, deck);
        if (bytes is not null)
        {
            surface.SetKeyImage(physicalIndex, bytes);
        }
        else
        {
            surface.ClearKey(physicalIndex);
        }
    }

    /// <summary>
    /// Physical push-in feedback (Elgato-software parity; the hardware has no
    /// built-in animation). Runs after HandleSlotAction so the fire-and-forget
    /// action dispatch (or a folder/page nav's own PushCurrentView) is never
    /// delayed by this method's image work. Skipped entirely for a
    /// folder/page-nav key, since PushCurrentView already repainted it with
    /// the new view - there is nothing on that key left to show pressed. A
    /// monitoring key is marked held (so RefreshMonitoringKeys skips it for
    /// the tick's duration) and gets a pressed inset rendered from
    /// _monitoringLastPushedBytes, its own last-rendered tile - silently
    /// skipped if the tile has never been rendered yet.
    /// </summary>
    private void HandlePressVisual(IStreamDeckSurface surface, int physicalIndex, int page, List<int> folderPath, int slotIndex, DeckSlot slot)
    {
        if (slot.Folder is not null || slot.Action?.Type == "page")
        {
            return;
        }

        var serial = surface.Serial;
        MarkKeyHeld(serial, physicalIndex);
        if (slot.Action?.Type == "monitoring")
        {
            PushMonitoringPressedVariant(surface, physicalIndex, page, folderPath, slotIndex, slot);
            return;
        }

        var deck = _store.Load().StreamDeck.Decks.TryGetValue(serial, out var d) ? d : null;
        var (bytes, hash) = ResolveSlotImage(serial, page, folderPath, slotIndex, slot, deck);
        if (bytes is null || hash is null)
        {
            return;
        }

        var pressed = GetOrRenderPressedVariant(hash, bytes, surface.Model);
        if (pressed is not null)
        {
            surface.SetKeyImage(physicalIndex, pressed);
        }
    }

    /// <summary>
    /// Renders and pushes a monitoring key's pressed inset from its own
    /// last-rendered tile. The pressed-cache key folds in a content hash
    /// (not just the monitoring key) since, unlike an uploaded image, a
    /// monitoring tile's bytes change every tick; falls back to hashing
    /// bytes on the spot when ForceMonitoringKeyRefresh has dropped the
    /// tracked hash (see _monitoringLastPushedBytes) rather than requiring
    /// both to be present.
    /// </summary>
    private void PushMonitoringPressedVariant(IStreamDeckSurface surface, int physicalIndex, int page, List<int> folderPath, int slotIndex, DeckSlot slot)
    {
        var monitoringKey = BuildMonitoringKey(surface.Serial, page, DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex));
        if (!_monitoringLastPushedBytes.TryGetValue(monitoringKey, out var bytes))
        {
            return;
        }
        var hash = _monitoringLastHash.TryGetValue(monitoringKey, out var trackedHash)
            ? trackedHash
            : ComputeFnv1aHash(bytes);

        var pressed = GetOrRenderPressedVariant($"{monitoringKey}:{hash:x8}", bytes, surface.Model);
        if (pressed is not null)
        {
            surface.SetKeyImage(physicalIndex, pressed);
        }
    }

    private void MarkKeyHeld(string serial, int physicalIndex)
    {
        if (!_heldKeysBySerial.TryGetValue(serial, out var held))
        {
            held = new HashSet<int>();
            _heldKeysBySerial[serial] = held;
        }
        held.Add(physicalIndex);
    }

    /// <summary>True (and clears the mark) only if this physical key was actually marked held by HandlePressVisual.</summary>
    private bool UnmarkKeyHeld(string serial, int physicalIndex) =>
        _heldKeysBySerial.TryGetValue(serial, out var held) && held.Remove(physicalIndex);

    private bool IsKeyHeld(string serial, int physicalIndex) =>
        _heldKeysBySerial.TryGetValue(serial, out var held) && held.Contains(physicalIndex);

    /// <summary>Drops the last-pushed hash for one monitoring key so the next RefreshMonitoringKeys tick repaints it even if the quantized reading is unchanged from before the hold suppressed it.</summary>
    private void ForceMonitoringKeyRefresh(string serial, int page, List<int> folderPath, int slotIndex)
    {
        var slotPath = DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex);
        _monitoringLastHash.Remove(BuildMonitoringKey(serial, page, slotPath));
    }

    /// <summary>An unassigned key: no action, no folder, and no explicit background color, so it renders off (blank black) rather than an uploaded fill. A color-only slot stays a decorative colored key.</summary>
    private static bool IsBlankOffSlot(DeckSlot slot) =>
        slot.Action is null && slot.Folder is null && string.IsNullOrEmpty(slot.Color);

    /// <summary>
    /// Resolves the wire bytes currently mapped to a leaf/toggle slot (state
    /// "0" or "1", matching PushCurrentView's per-key resolution) plus the
    /// content hash they were stored under, keyed by the page-qualified v2
    /// ImageRefs path (DeckConfigNavigation.BuildImageRefSlotPath) - or
    /// (null, null) when unmapped. A legacy pre-v2 key never matches here,
    /// so it renders as unmapped until the next editor sync re-uploads it
    /// under its v2 key.
    /// </summary>
    private (byte[]? Bytes, string? Hash) ResolveSlotImage(string serial, int page, List<int> folderPath, int slotIndex, DeckSlot slot, PhysicalDeckSettings? deck)
    {
        var latchKey = BuildLatchKey(serial, folderPath, slotIndex);
        var state = slot.Action?.Type == "toggle" && _executor.IsToggleOn(slot.Action.State, latchKey) ? "1" : "0";
        var slotPath = DeckConfigNavigation.BuildImageRefSlotPath(page, folderPath, slotIndex);
        var hash = deck is not null && deck.ImageRefs.TryGetValue($"{slotPath}/{state}", out var h) ? h : null;
        var bytes = hash is not null ? _imageCache.Load(serial, hash) : null;
        return (bytes, hash);
    }

    /// <summary>Covers a handful of distinct source images held in memory at once without unbounded growth; a cache miss just re-renders.</summary>
    private const int PressedImageCacheCapacity = 64;

    private readonly Dictionary<string, byte[]> _pressedImageCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pressedImageLru = new();

    /// <summary>Renders (or returns the cached) pushed-in variant of a source key image, keyed by the source's content hash so a given upload is scaled/re-encoded at most once.</summary>
    private byte[]? GetOrRenderPressedVariant(string hash, byte[] sourceWireBytes, StreamDeckModel model)
    {
        if (_pressedImageCache.TryGetValue(hash, out var cached))
        {
            _pressedImageLru.Remove(hash);
            _pressedImageLru.AddLast(hash);
            return cached;
        }

        var rendered = RenderPressedVariant(sourceWireBytes, model);
        if (rendered is null)
        {
            return null;
        }

        _pressedImageCache[hash] = rendered;
        _pressedImageLru.AddLast(hash);
        if (_pressedImageCache.Count > PressedImageCacheCapacity)
        {
            var oldest = _pressedImageLru.First!.Value;
            _pressedImageLru.RemoveFirst();
            _pressedImageCache.Remove(oldest);
        }
        return rendered;
    }

    private static byte[]? RenderPressedVariant(byte[] wireBytes, StreamDeckModel model)
    {
        if (model.ImageFormat == StreamDeckImageFormat.None)
        {
            return null;
        }
        try
        {
            using var decoded = Image.Load<Rgba32>(wireBytes);
            using var pressed = PressedKeyRenderer.Render(decoded);
            return model.ImageFormat switch
            {
                StreamDeckImageFormat.Bmp => BmpEncoder.Encode(RenderKit.ToRgb24(pressed), pressed.Width, pressed.Height),
                StreamDeckImageFormat.Jpeg => RenderKit.EncodeJpeg(pressed),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[streamdeck] pressed-variant render failed: {ex.Message}");
            return null;
        }
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

    /// <summary>A monitoring slot visible on a connected, awake deck this tick.</summary>
    private readonly struct MonitoringKeyRef
    {
        public readonly IStreamDeckSurface Surface;
        public readonly int KeyIndex;
        public readonly string HistoryKey;
        public readonly DeckSlot Slot;
        public readonly int Orientation;

        public MonitoringKeyRef(IStreamDeckSurface surface, int keyIndex, string historyKey, DeckSlot slot, int orientation)
        {
            Surface = surface;
            KeyIndex = keyIndex;
            HistoryKey = historyKey;
            Slot = slot;
            Orientation = orientation;
        }
    }

    /// <summary>
    /// Samples every visible monitoring key's sensor into its history buffer
    /// (cheap - a cached-snapshot read), then renders and pushes a capped,
    /// round-robin subset over HID (the expensive, rate-limited part).
    /// Skips a deck entirely while it is asleep/blanked - nothing is visible,
    /// so there is nothing to sample or push until the next wake.
    /// </summary>
    private void RefreshMonitoringKeys()
    {
        var snapshot = _store.Load();
        var settings = snapshot.StreamDeck;
        var tempUnit = snapshot.Units.MonitoringTempUnit;
        var numberFormat = snapshot.Units.NumberFormat;
        var visible = new List<MonitoringKeyRef>();

        foreach (var surface in _surfaces.Values)
        {
            if (!surface.IsConnected)
            {
                continue;
            }
            if (_asleep.TryGetValue(surface.Serial, out var asleep) && asleep)
            {
                continue;
            }
            if (!settings.Decks.TryGetValue(surface.Serial, out var deck))
            {
                continue;
            }
            var config = deck.Deck;
            var page = ClampCurrentPageLocked(surface.Serial, config);
            var folderPath = _folderPathsBySerial.TryGetValue(surface.Serial, out var fp) ? fp : new List<int>();
            var view = DeckConfigNavigation.ResolveView(config, page, folderPath);
            if (view is null)
            {
                continue;
            }

            var inFolder = folderPath.Count > 0;
            for (var key = 0; key < surface.Model.KeyCount; key++)
            {
                if (inFolder && key == 0)
                {
                    continue;
                }
                var slotIndex = inFolder ? key - 1 : key;
                if (slotIndex < 0 || slotIndex >= view.Count)
                {
                    continue;
                }
                var slot = view[slotIndex];
                if (slot.Action?.Type != "monitoring")
                {
                    continue;
                }
                var slotPath = DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex);
                var historyKey = BuildMonitoringKey(surface.Serial, page, slotPath);
                if (_monitoringPaintedThisTick.Contains(historyKey))
                {
                    // A PushCurrentView call earlier in this same Tick()
                    // already sampled and pushed this key (a connect or a
                    // simulated-deck nav both run inside Tick()); sampling it
                    // again here would add a second sample within the same
                    // instant, which changes a history-length-sensitive
                    // style's rendered pixels even though the reading has
                    // not changed and forces a redundant re-push.
                    continue;
                }
                visible.Add(new MonitoringKeyRef(surface, key, historyKey, slot, deck.Orientation));
            }
        }

        var sampled = new HardwareSensor?[visible.Count];
        for (var i = 0; i < visible.Count; i++)
        {
            sampled[i] = SampleMonitoringHistory(visible[i]);
        }

        if (visible.Count == 0)
        {
            return;
        }

        var pushCount = Math.Min(MonitoringPushCapPerTick, visible.Count);
        for (var i = 0; i < pushCount; i++)
        {
            var idx = (_monitoringRoundRobinCursor + i) % visible.Count;
            PushMonitoringKey(visible[idx], sampled[idx], tempUnit, numberFormat);
        }
        _monitoringRoundRobinCursor = (_monitoringRoundRobinCursor + pushCount) % visible.Count;
    }

    /// <summary>
    /// Does not append while the sensor is unresolved, so a sensor that
    /// disappears and later returns resumes its graph from the last real
    /// shape instead of dragging in a zero-trough; an empty buffer stays
    /// empty until the sensor first resolves.
    /// </summary>
    private HardwareSensor? SampleMonitoringHistory(MonitoringKeyRef key)
    {
        var action = key.Slot.Action!;
        var sensor = SensorSnapshotResolver.Resolve(_sensors, action.Category ?? "", action.Sensor ?? "");
        if (sensor is null)
        {
            return null;
        }
        if (!_monitoringHistory.TryGetValue(key.HistoryKey, out var history))
        {
            history = new List<float>(MonitoringHistoryLength);
            _monitoringHistory[key.HistoryKey] = history;
        }
        history.Add(sensor.Value);
        if (history.Count > MonitoringHistoryLength)
        {
            history.RemoveAt(0);
        }
        return sensor;
    }

    /// <summary>Value text shown when the configured sensor id no longer resolves.</summary>
    private const string UnresolvedSensorValueText = "--";

    /// <summary>
    /// Builds the tile content for a monitoring key: name/value/history text
    /// resolved for the given sensor reading (or the unresolved placeholder
    /// shape when sensor is null), plus the slot's style/domain/title
    /// overrides. Name and ValueText mirror nexus-web's DeckMonitoringCell
    /// (DeckMonitoringFormat.ResolveLabel/ResolveValueText) so the physical
    /// key and the touch-panel tile read the same for the same sensor;
    /// tempUnit/numberFormat are the caller's single-snapshot read of
    /// Units.MonitoringTempUnit/NumberFormat, matching useUnitPrefs() on the
    /// web side. Shared by the pass-1 placeholder (sensor null) and the real
    /// render.
    /// </summary>
    private MonitoringTileInput BuildMonitoringTileInput(MonitoringKeyRef key, HardwareSensor? sensor, string tempUnit, string numberFormat)
    {
        var action = key.Slot.Action!;
        string name;
        string valueText;
        string sensorType;
        List<float> historyForRender;

        if (sensor is null)
        {
            name = "";
            valueText = UnresolvedSensorValueText;
            sensorType = "";
            historyForRender = new List<float>();
        }
        else
        {
            var history = _monitoringHistory.TryGetValue(key.HistoryKey, out var h) ? h : new List<float>();
            // Rounded coarser than raw sensor jitter, so a visually-unchanged
            // tile hashes the same and is not re-pushed over HID every tick.
            historyForRender = new List<float>(history.Count);
            foreach (var sample in history)
            {
                historyForRender.Add(MathF.Round(sample, 1));
            }
            name = DeckMonitoringFormat.ResolveLabel(action.Category, sensor.Name);
            valueText = DeckMonitoringFormat.ResolveValueText(sensor, tempUnit, numberFormat);
            sensorType = sensor.Type;
        }

        var title = key.Slot.Title;
        return new MonitoringTileInput
        {
            Name = name,
            LabelText = action.LabelText,
            ShowName = action.ShowName ?? true,
            ValueText = valueText,
            SensorType = sensorType,
            History = historyForRender,
            Style = MonitoringTileRenderer.ParseStyle(action.Style),
            Scale = action.Scale,
            Min = (float?)action.Min,
            Max = (float?)action.Max,
            AccentColorHex = action.Color,
            BackgroundColorHex = key.Slot.Color,
            TitleFont = title?.Font,
            TitleSize = title?.Size,
            TitleBold = title?.Bold ?? false,
            TitleItalic = title?.Italic ?? false,
            TitleColorHex = title?.Color,
        };
    }

    /// <summary>Renders a tile input to this surface's wire bytes (oriented, transformed, encoded), or null when the encode fails or does not fit the model's wire length.</summary>
    private static byte[]? RenderMonitoringTileWireBytes(MonitoringTileInput input, StreamDeckModel model, int orientation)
    {
        using var rendered = MonitoringTileRenderer.Render(input, model.KeyPixelSize);
        return DeckImageToWireBytes(rendered, model, orientation);
    }

    /// <summary>Orients (user rotation), applies the model's fixed wire transform, and encodes a square rendered key image to this model's wire bytes, or null when the encode fails or the length does not fit the model.</summary>
    private static byte[]? DeckImageToWireBytes(Image<Rgba32> rendered, StreamDeckModel model, int orientation)
    {
        var raw = new DeckRawImage(rendered.Width, rendered.Height, RenderKit.ToRgba32Bytes(rendered));
        var oriented = DeckKeyTransformer.ApplyOrientation(raw, orientation);
        var transformed = DeckKeyTransformer.ApplyKeyTransform(oriented, DeckKeyTransformer.ParseTransform(model.Transform));

        byte[] wireBytes;
        using (var transformedImage = RenderKit.FromRgba32Bytes(transformed.Data, transformed.Width, transformed.Height))
        {
            wireBytes = model.ImageFormat switch
            {
                StreamDeckImageFormat.Bmp => BmpEncoder.Encode(RenderKit.ToRgb24(transformedImage), transformed.Width, transformed.Height),
                StreamDeckImageFormat.Jpeg => RenderKit.EncodeJpeg(transformedImage),
                _ => Array.Empty<byte>(),
            };
        }

        return wireBytes.Length == 0 || !model.IsValidWireImageLength(wireBytes.Length) ? null : wireBytes;
    }

    /// <summary>
    /// Pushes the same unresolved-sensor placeholder BuildMonitoringTileInput
    /// renders for a null sensor - a background-filled tile honoring
    /// slot.Color, no name/value/history - as an immediate first-pass repaint
    /// on navigation, so a monitoring key never shows the previous view's
    /// pixels while the real tile's sensor sample + render (pass 2) is still
    /// pending. Unconditional: no hash check, no _monitoringLastHash/
    /// _monitoringLastPushedBytes update - the very next PushMonitoringKey
    /// call for this same key overwrites both.
    /// </summary>
    private void PushMonitoringPlaceholder(MonitoringKeyRef key, string tempUnit, string numberFormat)
    {
        if (IsKeyHeld(key.Surface.Serial, key.KeyIndex))
        {
            return;
        }
        var input = BuildMonitoringTileInput(key, sensor: null, tempUnit, numberFormat);
        var wireBytes = RenderMonitoringTileWireBytes(input, key.Surface.Model, key.Orientation);
        if (wireBytes is not null)
        {
            key.Surface.SetKeyImage(key.KeyIndex, wireBytes);
        }
    }

    /// <summary>
    /// Renders and pushes one monitoring key, skipping the HID write when the
    /// encoded wire bytes hash the same as the last push (a pixel-identical
    /// tile - quantizing the history before render is what makes that hash
    /// stable tick over tick for an unchanged reading). A sensor that no
    /// longer resolves renders a placeholder tile rather than being skipped,
    /// so a stale image from a previous view is not left on the key forever.
    /// Never touches ImageRefs/StreamDeckImageCache - monitoring frames
    /// change every tick and are pushed as in-memory bytes. Skips the push
    /// (sampling already happened in SampleMonitoringHistory) while the key
    /// is physically held, so the tick never races HandlePressVisual/HandleKeyUp
    /// for the same pixels; ForceMonitoringKeyRefresh drops the last-pushed
    /// hash on release so the next tick repaints it regardless.
    /// </summary>
    private void PushMonitoringKey(MonitoringKeyRef key, HardwareSensor? sensor, string tempUnit, string numberFormat)
    {
        if (IsKeyHeld(key.Surface.Serial, key.KeyIndex))
        {
            return;
        }

        var input = BuildMonitoringTileInput(key, sensor, tempUnit, numberFormat);
        var wireBytes = RenderMonitoringTileWireBytes(input, key.Surface.Model, key.Orientation);
        if (wireBytes is null)
        {
            return;
        }

        var hash = ComputeFnv1aHash(wireBytes);
        if (_monitoringLastHash.TryGetValue(key.HistoryKey, out var lastHash) && lastHash == hash)
        {
            return;
        }

        if (key.Surface.SetKeyImage(key.KeyIndex, wireBytes))
        {
            _monitoringLastHash[key.HistoryKey] = hash;
            _monitoringLastPushedBytes[key.HistoryKey] = wireBytes;
        }
    }

    /// <summary>32-bit FNV-1a, same algorithm HidStreamDeckSurface.StableIdFromPath uses - not cryptographic, only used for tick-to-tick change detection.</summary>
    private static uint ComputeFnv1aHash(byte[] bytes)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var b in bytes)
            {
                h ^= b;
                h *= 16777619;
            }
            return h;
        }
    }

    /// <summary>Page-qualified: BuildSlotPath alone is not unique across a deck's pages.</summary>
    private static string BuildMonitoringKey(string serial, int page, string slotPath) => $"{serial}:{page}:{slotPath}";

    private void RemoveMonitoringStateForSerial(string serial)
    {
        var prefix = serial + ":";
        foreach (var k in _monitoringHistory.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _monitoringHistory.Remove(k);
        }
        InvalidateMonitoringHashesForSerial(serial);
    }

    /// <summary>
    /// Drops last-pushed hashes for this serial so the next RefreshMonitoringKeys
    /// tick repaints every visible monitoring key even if its quantized reading
    /// is unchanged from before the view changed. Without this, a key that goes
    /// monitoring -> non-monitoring -> monitoring again across a nav/config
    /// change keeps whatever foreign image PushCurrentView (or the other page's
    /// monitoring render) last put on it, because the hash still matches.
    /// </summary>
    private void InvalidateMonitoringHashesForSerial(string serial)
    {
        var prefix = serial + ":";
        foreach (var k in _monitoringLastHash.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _monitoringLastHash.Remove(k);
            _monitoringLastPushedBytes.Remove(k);
        }
    }

    /// <summary>
    /// Drops history/last-pushed-hash entries for this serial whose (page,
    /// slotPath) no longer resolves to a monitoring-typed slot in the given
    /// config, so a config edit that deletes or retypes a monitoring slot
    /// (or deletes a page) does not orphan its ring buffer while the deck
    /// stays connected. Caller must hold _lock.
    /// </summary>
    private void EvictOrphanedMonitoringEntriesForSerial(string serial, DeckConfig config)
    {
        var prefix = serial + ":";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _monitoringHistory.Keys)
        {
            if (k.StartsWith(prefix, StringComparison.Ordinal))
            {
                keys.Add(k);
            }
        }
        foreach (var k in _monitoringLastHash.Keys)
        {
            if (k.StartsWith(prefix, StringComparison.Ordinal))
            {
                keys.Add(k);
            }
        }
        foreach (var key in keys)
        {
            if (MonitoringKeyStillResolves(config, key, prefix))
            {
                continue;
            }
            _monitoringHistory.Remove(key);
            _monitoringLastHash.Remove(key);
            _monitoringLastPushedBytes.Remove(key);
        }
    }

    /// <summary>Parses a "{serial}:{page}:{slotPath}" monitoring key (see BuildMonitoringKey) and resolves it against config.</summary>
    private static bool MonitoringKeyStillResolves(DeckConfig config, string historyKey, string serialPrefix)
    {
        var remainder = historyKey.AsSpan(serialPrefix.Length);
        var separator = remainder.IndexOf(':');
        if (separator < 0 || !int.TryParse(remainder[..separator], out var page))
        {
            return false;
        }
        var indices = DeckConfigNavigation.ParseSlotPath(remainder[(separator + 1)..].ToString());
        if (indices is null)
        {
            return false;
        }
        var slot = DeckConfigNavigation.ResolveSlot(config, page, indices);
        return slot?.Action?.Type == "monitoring";
    }

    private void PushCurrentView(IStreamDeckSurface surface)
    {
        InvalidateMonitoringHashesForSerial(surface.Serial);
        if (!_folderPathsBySerial.TryGetValue(surface.Serial, out var folderPath))
        {
            folderPath = new List<int>();
        }
        var snapshot = _store.Load();
        var settings = snapshot.StreamDeck;
        var tempUnit = snapshot.Units.MonitoringTempUnit;
        var numberFormat = snapshot.Units.NumberFormat;
        settings.Decks.TryGetValue(surface.Serial, out var deck);
        var config = deck?.Deck ?? new DeckConfig();
        EvictOrphanedMonitoringEntriesForSerial(surface.Serial, config);
        var page = ClampCurrentPageLocked(surface.Serial, config);

        var view = DeckConfigNavigation.ResolveView(config, page, folderPath);
        if (view is null)
        {
            folderPath = new List<int>();
            _folderPathsBySerial[surface.Serial] = folderPath;
            view = DeckConfigNavigation.ResolveView(config, page, folderPath) ?? new List<DeckSlot>();
        }

        var inFolder = folderPath.Count > 0;
        var monitoringKeys = new List<MonitoringKeyRef>();

        // Pass 1: every key goes out immediately in key order - static images
        // (or ClearKey) for leaf/toggle/folder slots, and an empty
        // placeholder (no sensor sample, so no per-key sensor-lookup cost)
        // for a monitoring slot - so the whole view goes clean the instant a
        // nav lands, instead of later keys sitting on the previous view's
        // pixels while earlier keys' sensor sample + render (pass 2, below)
        // are still running.
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
            if (slot.Action?.Type == "monitoring")
            {
                var slotPath = DeckConfigNavigation.BuildSlotPath(folderPath, slotIndex);
                var keyRef = new MonitoringKeyRef(surface, key, BuildMonitoringKey(surface.Serial, page, slotPath), slot, deck?.Orientation ?? 0);
                PushMonitoringPlaceholder(keyRef, tempUnit, numberFormat);
                monitoringKeys.Add(keyRef);
                continue;
            }
            // An unassigned key renders off (black), matching the deck's own
            // firmware, instead of the category-default fill the editor uploads
            // for an empty slot.
            if (IsBlankOffSlot(slot))
            {
                surface.ClearKey(key);
                continue;
            }

            var (bytes, _) = ResolveSlotImage(surface.Serial, page, folderPath, slotIndex, slot, deck);
            if (bytes is not null)
            {
                surface.SetKeyImage(key, bytes);
            }
            else
            {
                surface.ClearKey(key);
            }
        }

        // Pass 2: sample and render the real monitoring content onto the
        // now-blank tiles. The invalidation call above already dropped every
        // monitoring key's last-pushed hash, so PushMonitoringKey always
        // repaints regardless of whether the sampled reading matches the
        // prior view's.
        foreach (var keyRef in monitoringKeys)
        {
            var sensor = SampleMonitoringHistory(keyRef);
            PushMonitoringKey(keyRef, sensor, tempUnit, numberFormat);
            _monitoringPaintedThisTick.Add(keyRef.HistoryKey);
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

    /// <summary>
    /// Advances every in-progress blank-key hold by one animation frame:
    /// fills the ring toward HoldToEditMs, and once elapsed reaches it, fires
    /// the open-editor intent and blanks the key. Public so tests step it
    /// deterministically with a manual clock, like Tick(). A hold whose
    /// surface has vanished is dropped.
    /// </summary>
    public void AnimateHolds()
    {
        lock (_lock)
        {
            if (_activeHolds.Count == 0)
            {
                _anyHoldActive = false;
                return;
            }
            var now = _clock.GetUtcNow();
            foreach (var serial in _activeHolds.Keys.ToList())
            {
                var holds = _activeHolds[serial];
                var surface = FindBySerialLocked(serial);
                if (surface is null || !surface.IsConnected)
                {
                    _activeHolds.Remove(serial);
                    continue;
                }
                foreach (var physicalIndex in holds.Keys.ToList())
                {
                    var hold = holds[physicalIndex];
                    var fraction = (float)Math.Clamp((now - hold.StartedAt).TotalMilliseconds / HoldToEditMs, 0.0, 1.0);
                    if (fraction >= 1f)
                    {
                        FireHoldEdit(surface, hold);
                        surface.ClearKey(physicalIndex);
                        holds.Remove(physicalIndex);
                        continue;
                    }
                    var frameIndex = (int)(fraction * HoldRingSteps);
                    if (frameIndex != hold.LastFrameIndex)
                    {
                        PushHoldFrame(surface, physicalIndex, frameIndex);
                        hold.LastFrameIndex = frameIndex;
                    }
                }
                if (holds.Count == 0)
                {
                    _activeHolds.Remove(serial);
                }
            }
            _anyHoldActive = _activeHolds.Count > 0;
        }
    }

    /// <summary>Begins a blank-key hold: shows the fill ring's first frame and starts the animation clock. Caller holds _lock (input path).</summary>
    private void StartHoldEdit(IStreamDeckSurface surface, int physicalIndex, int page, List<int> folderPath, int slotIndex)
    {
        var serial = surface.Serial;
        if (!_activeHolds.TryGetValue(serial, out var holds))
        {
            holds = new Dictionary<int, HoldEditState>();
            _activeHolds[serial] = holds;
        }
        holds[physicalIndex] = new HoldEditState
        {
            Page = page,
            FolderPath = new List<int>(folderPath),
            SlotIndex = slotIndex,
            StartedAt = _clock.GetUtcNow(),
            LastFrameIndex = 0,
        };
        _anyHoldActive = true;
        PushHoldFrame(surface, physicalIndex, 0);
    }

    /// <summary>Drops an in-progress (not-yet-fired) hold for this exact key, returning true if one was tracked. Caller holds _lock.</summary>
    private bool TryEndHoldEdit(string serial, int physicalIndex)
    {
        if (!_activeHolds.TryGetValue(serial, out var holds) || !holds.Remove(physicalIndex))
        {
            return false;
        }
        if (holds.Count == 0)
        {
            _activeHolds.Remove(serial);
        }
        _anyHoldActive = _activeHolds.Count > 0;
        return true;
    }

    private void RemoveAllHoldsForSerial(string serial)
    {
        if (_activeHolds.Remove(serial))
        {
            _anyHoldActive = _activeHolds.Count > 0;
        }
    }

    /// <summary>Test seam: true if a blank-key hold is currently animating on this exact key.</summary>
    internal bool HasActiveHold(string serial, int physicalIndex)
    {
        lock (_lock)
        {
            return _activeHolds.TryGetValue(serial, out var holds) && holds.ContainsKey(physicalIndex);
        }
    }

    /// <summary>
    /// Records the pending-edit intent, broadcasts the live "editRequest"
    /// frame, and opens/focuses the app off this thread (OpenApp may block on
    /// an interactive-session launch). Caller holds _lock.
    /// </summary>
    private void FireHoldEdit(IStreamDeckSurface surface, HoldEditState hold)
    {
        var serial = surface.Serial;
        var token = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _pendingEdit = new DeckPendingEdit(serial, hold.Page, new List<int>(hold.FolderPath), hold.SlotIndex, token, _clock.GetUtcNow());
        BroadcastEditRequest(serial, hold.Page, hold.FolderPath, hold.SlotIndex, token);
        ServiceLog.Info($"[streamdeck] hold-to-edit fired serial={serial} page={hold.Page} slot={hold.SlotIndex}");
        LastHoldFireTask = Task.Run(() =>
        {
            try { _executor.OpenApp(); }
            catch (Exception ex) { ServiceLog.Warn($"[streamdeck] hold-to-edit open-app failed serial={serial}: {ex.Message}"); }
        });
    }

    /// <summary>Test seam: the most recent fire-and-forget OpenApp dispatch, so a test can await the app-open side effect.</summary>
    internal Task? LastHoldFireTask { get; private set; }

    private void PushHoldFrame(IStreamDeckSurface surface, int physicalIndex, int frameIndex)
    {
        var orientation = _store.Load().StreamDeck.Decks.TryGetValue(surface.Serial, out var deck) ? deck.Orientation : 0;
        var bytes = GetOrRenderHoldFrame(surface.Model, orientation, frameIndex);
        if (bytes is not null)
        {
            surface.SetKeyImage(physicalIndex, bytes);
        }
    }

    private byte[]? GetOrRenderHoldFrame(StreamDeckModel model, int orientation, int frameIndex)
    {
        if (model.ImageFormat == StreamDeckImageFormat.None)
        {
            return null;
        }
        var cacheKey = $"{model.ProductId}:{orientation}:{frameIndex}";
        if (_holdFrameCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        var fraction = (float)frameIndex / HoldRingSteps;
        try
        {
            using var rendered = DeckHoldPromptRenderer.Render(fraction, model.KeyPixelSize);
            var bytes = DeckImageToWireBytes(rendered, model, orientation);
            if (bytes is not null)
            {
                _holdFrameCache[cacheKey] = bytes;
            }
            return bytes;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[streamdeck] hold-frame render failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads the current pending blank-key hold-to-edit intent, or false when none is pending or the last one has aged past PendingEditTtl.</summary>
    internal bool TryGetPendingEdit(out DeckPendingEdit edit)
    {
        lock (_lock)
        {
            if (_pendingEdit is { } pending && _clock.GetUtcNow() - pending.CreatedAt < PendingEditTtl)
            {
                edit = pending;
                return true;
            }
            edit = default;
            return false;
        }
    }

    private void BroadcastEditRequest(string serial, int page, List<int> folderPath, int keyIndex, long token) =>
        PanelTopics.BroadcastStreamDeck(_hub, new StreamDeckChangedFrame
        {
            Kind = "editRequest",
            Serial = serial,
            Page = page,
            FolderPath = new List<int>(folderPath),
            KeyIndex = keyIndex,
            Token = token,
        });

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
                RestoreBrightnessAndResetBeforeDisconnect(surface);
                StopInputReader(key);
                surface.Dispose();
            }
            _surfaces.Clear();
            _lastKeyStates.Clear();
            _folderPathsBySerial.Clear();
            _lastInputAt.Clear();
            _asleep.Clear();
            _currentPageBySerial.Clear();
            _heldKeysBySerial.Clear();
            _activeHolds.Clear();
            _anyHoldActive = false;
            _monitoringHistory.Clear();
            _monitoringLastHash.Clear();
            _monitoringLastPushedBytes.Clear();
            if (hadSurfaces)
            {
                BroadcastDecksChanged(null);
            }
        }
    }

    /// <summary>Never a literal 0% - the deck may be blanked at runtime brightness 0 by ApplySleepAfterIdle even though its persisted setting is not.</summary>
    private const int MinDisconnectBrightness = 1;

    /// <summary>
    /// Restores persisted brightness and fires a firmware Reset() (the
    /// built-in Elgato boot logo), so a deck shows something static instead
    /// of freezing on its last live frame. Caller must hold _lock; shared by
    /// DisconnectAll (deck torn down after) and ResetConnectedSurfacesForShutdown
    /// (deck left tracked, for a fast process exit); both run during host
    /// shutdown, so it must stay fast.
    /// </summary>
    private void RestoreBrightnessAndResetBeforeDisconnect(IStreamDeckSurface surface)
    {
        var settings = _store.Load().StreamDeck;
        settings.Decks.TryGetValue(surface.Serial, out var deck);
        var brightness = Math.Clamp(deck?.Brightness ?? PhysicalDeckSettings.DefaultBrightness, MinDisconnectBrightness, 100);
        surface.SetBrightness(brightness);
        surface.Reset();
        ServiceLog.Info($"[streamdeck] reset before disconnect (serial={surface.Serial})");
    }

    /// <summary>
    /// Restores brightness and fires a firmware Reset() on every connected
    /// real surface without tearing down tracked state. FastServiceShutdown
    /// calls this on a real Windows quit (SCM stop, /service/stop, tray shut
    /// down, factory reset, GPU-change restart): that path exits via
    /// Environment.Exit after a bounded concurrent teardown and never runs
    /// ExecuteAsync's post-loop DisconnectAll, so without this the deck would
    /// otherwise freeze on its last live frame instead of showing the boot
    /// logo. Exception-safe per deck so one wedged surface does not block the
    /// others under the shutdown's hard timeout.
    /// </summary>
    public void ResetConnectedSurfacesForShutdown()
    {
        lock (_lock)
        {
            foreach (var (key, surface) in _surfaces)
            {
                if (key == SimulatedKey)
                {
                    continue;
                }
                try
                {
                    RestoreBrightnessAndResetBeforeDisconnect(surface);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[streamdeck] shutdown reset failed serial={surface.Serial}: {ex.Message}");
                }
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
