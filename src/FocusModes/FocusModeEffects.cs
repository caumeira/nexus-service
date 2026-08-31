using Microsoft.Extensions.Hosting;
using Nexus.Service.Notifications;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.FocusModes;

/// <summary>Applies the active focus mode's push-side effects as desired-state writes, so a toggle flipped mid-session both takes hold and undoes itself.</summary>
public sealed class FocusModeEffects : IHostedService
{
    private readonly FocusModeState _state;
    private readonly IConfigStore _config;
    private readonly MultiplexHub _hub;
    private readonly IY70Provider _y70;
    private readonly PanelKioskLauncher _kiosk;
    private readonly StreamedPanelCoordinator? _streams;

    private bool _panelsOffApplied;

    public FocusModeEffects(
        FocusModeState state, IConfigStore config, MultiplexHub hub,
        IY70Provider y70, PanelKioskLauncher kiosk,
        StreamedPanelCoordinator? streams = null)
    {
        _state = state;
        _config = config;
        _hub = hub;
        _y70 = y70;
        _kiosk = kiosk;
        _streams = streams;
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
        Apply(null);
        return Task.CompletedTask;
    }

    private void OnActiveChanged(FocusModeSettings? mode)
    {
        Apply(mode);
        PanelTopics.BroadcastFocus(_hub);
    }

    private void Apply(FocusModeSettings? mode)
    {
        ApplyNotificationHold(mode?.HoldNotifications == true);
        FocusNetworkGate.Set(mode?.HoldBackgroundTraffic == true);
        ApplyPanelsOff(mode?.TurnPanelDisplaysOff == true);
    }

    private static void ApplyNotificationHold(bool hold)
    {
        if (hold) NotificationGate.Hold(NotificationGate.ReasonFocusMode);
        else _ = NotificationGate.ReleaseAsync(NotificationGate.ReasonFocusMode);
    }

    /// <summary>
    /// Stops rendering the panels Nexus draws itself and puts their displays to
    /// sleep. Q-Series and Tryx render on-device and are deliberately untouched:
    /// nothing of ours is drawing them, so there is no resource to reclaim.
    /// </summary>
    private void ApplyPanelsOff(bool off)
    {
        if (_panelsOffApplied == off) return;
        _panelsOffApplied = off;

        try { _streams?.SetRenderingPaused(off); }
        catch (Exception ex) { ServiceLog.Warn($"[focus] stream pause failed: {ex.Message}"); }

        try
        {
            // Restore only what the pref would have opened anyway: PanelKioskLauncher.IsRunning
            // is a false negative from Session 0, so a blind Launch would open a kiosk on a
            // machine that never had one.
            if (off) _kiosk.Close();
            else if (LoadPanelAutoLaunch()) _kiosk.Launch();
        }
        catch (Exception ex) { ServiceLog.Warn($"[focus] kiosk toggle failed: {ex.Message}"); }

        try { _y70.SetGameModeScreenOff(off); }
        catch (Exception ex) { ServiceLog.Warn($"[focus] y70 screen power failed: {ex.Message}"); }
    }

    private bool LoadPanelAutoLaunch()
    {
        try { return _config.Load().Panel.AutoLaunch; }
        catch { return false; }
    }
}
