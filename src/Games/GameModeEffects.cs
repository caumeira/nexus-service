using Microsoft.Extensions.Hosting;
using Nexus.Service.Notifications;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Games;

/// <summary>Applies Game Mode's push-side effects as desired-state writes, so a toggle flipped mid-session both takes hold and undoes itself.</summary>
public sealed class GameModeEffects : IHostedService
{
    private readonly GameModeState _state;
    private readonly IConfigStore _config;
    private readonly MultiplexHub _hub;
    private readonly IY70Provider _y70;
    private readonly PanelKioskLauncher _kiosk;
    private readonly StreamedPanelCoordinator? _streams;
    private readonly Nexus.Service.QSeries.QSeriesPortWatcher? _qseries;

    private bool _displaysOffApplied;
    private bool _renderStopApplied;

    public GameModeEffects(
        GameModeState state, IConfigStore config, MultiplexHub hub,
        IY70Provider y70, PanelKioskLauncher kiosk,
        StreamedPanelCoordinator? streams = null,
        Nexus.Service.QSeries.QSeriesPortWatcher? qseries = null)
    {
        _state = state;
        _config = config;
        _hub = hub;
        _y70 = y70;
        _kiosk = kiosk;
        _streams = streams;
        _qseries = qseries;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _state.ActiveChanged += OnActiveChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state.ActiveChanged -= OnActiveChanged;
        // The panel override lives only in memory, so stopping without it would leave the panels dark.
        Apply(false);
        return Task.CompletedTask;
    }

    private void OnActiveChanged(bool active)
    {
        Apply(active);
        PanelTopics.BroadcastGameMode(_hub);
    }

    private void Apply(bool active)
    {
        var settings = LoadSettings();

        ApplyNotificationHold(active && settings.HoldNotifications);
        GameModeNetworkGate.Set(active && settings.HoldBackgroundNetwork);
        ApplyDisplaysOff(active && settings.TurnPanelDisplaysOff);
        ApplyRenderStop(active && settings.StopPanelRendering);
    }

    private static void ApplyNotificationHold(bool hold)
    {
        if (hold) NotificationGate.Hold(NotificationGate.ReasonGameMode);
        else _ = NotificationGate.ReleaseAsync(NotificationGate.ReasonGameMode);
    }

    private void ApplyDisplaysOff(bool off)
    {
        if (_displaysOffApplied == off) return;
        _displaysOffApplied = off;

        try { _y70.SetGameModeScreenOff(off); }
        catch (Exception ex) { ServiceLog.Warn($"[game-mode] y70 screen power failed: {ex.Message}"); }

        try { _qseries?.SetGameModeSleep(off); }
        catch (Exception ex) { ServiceLog.Warn($"[game-mode] q-series screen power failed: {ex.Message}"); }
    }

    private void ApplyRenderStop(bool stop)
    {
        if (_renderStopApplied == stop) return;
        _renderStopApplied = stop;

        try
        {
            // Restore only what the pref would have opened anyway: PanelKioskLauncher.IsRunning
            // is a false negative from Session 0, so a blind Launch would open a kiosk on a
            // machine that never had one.
            if (stop) _kiosk.Close();
            else if (LoadPanelAutoLaunch()) _kiosk.Launch();
        }
        catch (Exception ex) { ServiceLog.Warn($"[game-mode] kiosk toggle failed: {ex.Message}"); }

        try { _streams?.SetRenderingPaused(stop); }
        catch (Exception ex) { ServiceLog.Warn($"[game-mode] stream pause failed: {ex.Message}"); }
    }

    private bool LoadPanelAutoLaunch()
    {
        try { return _config.Load().Panel.AutoLaunch; }
        catch { return false; }
    }

    private GameModeSettings LoadSettings()
    {
        try { return _config.Load().GameMode ?? new GameModeSettings(); }
        catch { return new GameModeSettings(); }
    }
}
