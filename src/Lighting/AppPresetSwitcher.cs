using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Activates a lighting preset when an app it is bound to takes focus, and
/// restores the preset that was active before when focus moves to an unbound
/// app. Driven by <see cref="IScreenTimeProvider.FocusChanged"/> - the same
/// engine screen time runs on - so there is no second cadence: Windows and
/// macOS surface a change on their 2s jittered poll, Linux on a KWin event.
/// </summary>
public sealed class AppPresetSwitcher : BackgroundService
{
    private readonly IConfigStore _store;
    private readonly IScreenTimeProvider _screenTime;
    private readonly MultiplexHub _hub;
    private readonly ILightingDeviceProvider _lightingDevices;
    private readonly ILightingProvider _lighting;
    private readonly Rgb.RgbBridge? _bridge;
    private readonly Smart.SmartLightProvider _smart;
    private readonly Engine.LightingEngine _engine;
    private readonly TimeProvider _time;
    private readonly AppPresetFocusTracker _tracker = new();
    private readonly object _gate = new();
    private ITimer? _dwellTimer;
    private bool _subscribed;

    public AppPresetSwitcher(
        IConfigStore store,
        IScreenTimeProvider screenTime,
        MultiplexHub hub,
        ILightingDeviceProvider lightingDevices,
        ILightingProvider lighting,
        Rgb.RgbBridge? bridge,
        Smart.SmartLightProvider smart,
        Engine.LightingEngine engine)
        : this(store, screenTime, hub, lightingDevices, lighting, bridge, smart, engine, TimeProvider.System)
    {
    }

    internal AppPresetSwitcher(
        IConfigStore store,
        IScreenTimeProvider screenTime,
        MultiplexHub hub,
        ILightingDeviceProvider lightingDevices,
        ILightingProvider lighting,
        Rgb.RgbBridge? bridge,
        Smart.SmartLightProvider smart,
        Engine.LightingEngine engine,
        TimeProvider time)
    {
        _store = store;
        _screenTime = screenTime;
        _hub = hub;
        _lightingDevices = lightingDevices;
        _lighting = lighting;
        _bridge = bridge;
        _smart = smart;
        _engine = engine;
        _time = time;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _screenTime.FocusChanged += OnFocusChanged;
        _store.OnChanged += OnStoreChanged;
        _subscribed = true;

        // A binding saved while its app already holds focus produces no focus
        // event, so the store change is the other trigger.
        stoppingToken.Register(Unsubscribe);
        // Catch an app that was already focused when the service started.
        Schedule();
        return Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed) return;
            _subscribed = false;
            _screenTime.FocusChanged -= OnFocusChanged;
            _store.OnChanged -= OnStoreChanged;
            _dwellTimer?.Dispose();
            _dwellTimer = null;
        }
    }

    public override void Dispose()
    {
        Unsubscribe();
        base.Dispose();
    }

    private void OnFocusChanged() => Schedule();

    private void OnStoreChanged() => Schedule();

    /// <summary>Evaluates now (which records the candidate) and again once the
    /// dwell has elapsed (which acts on it), so a burst of alt-tabs collapses
    /// to a single evaluation.</summary>
    private void Schedule()
    {
        SafeTick();
        lock (_gate)
        {
            if (!_subscribed) return;
            _dwellTimer?.Dispose();
            _dwellTimer = _time.CreateTimer(
                _ => SafeTick(),
                null,
                AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(50),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app-presets] evaluation failed: {ex.Message}");
        }
    }

    internal void Tick()
    {
        var settings = _store.Load();
        var presets = settings.Lighting.LayoutPresets;
        if (!AnyBindings(presets))
        {
            // Not just an early-out: a pending restore target refers to a
            // binding that no longer exists, and holding it would restore an
            // arbitrarily stale preset once bindings come back.
            _tracker.Reset();
            return;
        }

        var focused = _screenTime.GetCurrentSession()?.Name ?? "";
        var target = _tracker.Decide(focused, settings.Lighting.ActiveLayoutPresetId, presets, MonotonicNowMs());
        if (target is null)
        {
            return;
        }

        if (!Routes.DevicesRoutes.ActivateLayoutPreset(
                target, _store, _hub, _lightingDevices, _lighting, _bridge, _smart, _engine))
        {
            Console.Error.WriteLine($"[app-presets] preset {target} vanished before it could be activated");
            _tracker.Reset();
        }
    }

    // Monotonic: an NTP correction must not stall the dwell (backward) or
    // short-circuit it (forward).
    private long MonotonicNowMs() =>
        (long)(_time.GetTimestamp() / (double)_time.TimestampFrequency * 1000.0);

    private static bool AnyBindings(System.Collections.Generic.List<LayoutPreset> presets)
    {
        foreach (var preset in presets)
        {
            if (preset.Apps is { Count: > 0 })
            {
                return true;
            }
        }
        return false;
    }

}
