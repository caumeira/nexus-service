using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
#endif

namespace Nexus.Service.Platform.Windows;

// Windows-only post-Build wiring for the tray icon, helper-pipe sync, and
// the app-window auto-launch on startup. Each piece is gated by the calling
// context (--service vs interactive) so the Session-0 daemon never tries
// to materialise an interactive NotifyIcon.
[SupportedOSPlatform("windows")]
internal static class TrayBootstrap
{
    // Interactive Windows session: hides console, shows tray with right-click menu.
    // Skipped under --service: Session 0 cannot show UI, so the tray must be a
    // separate user-session process (Phase 4: Nexus.exe --tray). Leaving the
    // tray init in here would create a stale NotifyIcon in Session 0.
    public static void ConfigureTray(WebApplication app)
    {
        var panelLauncher = app.Services.GetRequiredService<PanelKioskLauncher>();
        var store = app.Services.GetRequiredService<IConfigStore>();
        var desktopHostForTray = app.Services.GetRequiredService<PanelOverlayHostLauncher>();
        TrayIcon.Configure(
            9400,
            onExit: () =>
            {
                panelLauncher.Close();
                try { desktopHostForTray.Stop(); } catch { /* best-effort */ }
                Environment.Exit(0);
            },
            onTogglePanel: () =>
            {
                // Flip the AutoLaunch / Show Panel setting; the overlay-host
                // watcher mirrors it onto the nexus-overlay kiosk window. The
                // OnChanged cascade broadcasts /prefs on its own.
                store.Update(s => s.Panel.AutoLaunch = !s.Panel.AutoLaunch);
            },
            // Tray checkmark reflects actual kiosk-window state via FindWindow,
            // not the persisted setting — that way a dead/crashed overlay
            // shows unchecked even if AutoLaunch is still true.
            isPanelRunning: () => panelLauncher.IsRunning);

        var hub = app.Services.GetRequiredService<MultiplexHub>();
        TrayIcon.ConfigureDesktop(
            onToggleOverlayTopmost: () =>
            {
                store.Update(s => s.Overlay.AlwaysOnTop = !s.Overlay.AlwaysOnTop);
                PanelTopics.BroadcastPrefs(hub);
            },
            isOverlayTopmost: () => store.Load().Overlay.AlwaysOnTop,
            hasOverlayWidgets: () => store.Load().Overlay.Layout.Count > 0);

        TrayIcon.SetVisible(store.Load().Monitoring.ShowWindowsTrayIcon);

        store.OnChanged += () =>
        {
            try { TrayIcon.SetVisible(store.Load().Monitoring.ShowWindowsTrayIcon); }
            catch { /* best-effort */ }
        };
    }

#if WINDOWS
    // Service mode: the helper is a separate long-lived user-session process
    // connected over a named pipe. ShowWindowsTrayIcon no longer controls the
    // helper's existence (it always runs so providers like screen-time stay
    // alive); we push the visibility flip down the pipe and let the helper
    // hide/show its NotifyIcon in place. Also wires the "Shut down" item back
    // through the helper-pipe — the service's SCM DACL only grants
    // Authenticated Users QUERY_STATUS + START, not STOP, so the helper
    // can't sc.exe-stop us itself.
    public static void WireHelperPipe(WebApplication app)
    {
        var trayStore = app.Services.GetRequiredService<IConfigStore>();
        var helperRegistry = app.Services.GetRequiredService<HelperRegistry>();
        var lastVisible = trayStore.Load().Monitoring.ShowWindowsTrayIcon;

        // Push current state on every fresh helper connect: first bootstrap,
        // service restart, helper crash-and-respawn.
        helperRegistry.Connected += conn =>
        {
            try
            {
                var current = trayStore.Load().Monitoring.ShowWindowsTrayIcon;
                _ = TrayCommands.SetVisibleAsync(helperRegistry, current);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] initial state failed: {ex.Message}"); }
        };

        // Re-assert the Y70 panel's display orientation on every fresh helper
        // connect. Windows defaults a freshly attached portrait panel to
        // landscape; this drives it to the stored orientation (PortraitFlipped
        // by default) so the panel never comes up sideways — replacing the
        // legacy onboarding "Rotate" step. The Win32 ChangeDisplaySettingsEx
        // call runs in the helper (user session, where it can see the
        // monitors); a no-op when Windows is already in the target orientation.
        helperRegistry.Connected += conn =>
        {
            try
            {
                var orientation = trayStore.Load().Y70.Orientation;
                _ = OrientationCommands.SetAsync(helperRegistry, orientation);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[y70-sync] initial orientation failed: {ex.Message}"); }
        };

        trayStore.OnChanged += () =>
        {
            try
            {
                var nowVisible = trayStore.Load().Monitoring.ShowWindowsTrayIcon;
                if (nowVisible == lastVisible) return;
                lastVisible = nowVisible;
                _ = TrayCommands.SetVisibleAsync(helperRegistry, nowVisible);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] {ex.Message}"); }
        };

        helperRegistry.InboundEnvelope += (_, env) =>
        {
            if (env.Type != "service.requestStop") return;
            try { app.Lifetime.StopApplication(); }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] requestStop failed: {ex.Message}"); }
        };

        // Quitting must take the user-session UI with it: close the --app
        // window and exit the helper so the tray icon disappears. Without
        // this, "Stop Nexus" leaves an orphaned Edge --app window pointing at
        // a dead port and a stale tray icon in the user session.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                LifecycleCommands.SendShutdownAsync(helperRegistry, cts.Token).GetAwaiter().GetResult();
            }
            catch { /* helper may not be connected */ }
        });
    }
#endif

    // Wires the on-Started auto-launch of the dashboard window (or helper in
    // service mode), the on-Stopping panel-kiosk close + state flush, and
    // the background PawnIO install. Windows-only.
    public static void WireAppWindowAndPawnIo(WebApplication app, bool serviceMode, bool suppressStartupWindow)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (serviceMode)
            {
                Console.WriteLine("[nexus-service] startup window suppressed (LocalSystem session 0 has no interactive desktop)");
#if WINDOWS
                // Always launch the user-session helper. Its lifetime is decoupled
                // from any pref - the helper hosts the tray icon, screen-time
                // poller, media/brightness providers, etc.
                Nexus.Service.Lifecycle.UserHelperBootstrapper.EnsureLaunched();
#endif
            }
            else if (suppressStartupWindow)
            {
                Console.WriteLine("[nexus-service] startup window suppressed (--no-window)");
            }
            else
            {
                TrayIcon.OpenLocalWindow();
                Console.WriteLine("[nexus-service] app window launched");
            }
        });

        // Kill panel kiosk webview on any shutdown (Ctrl+C, Task Manager,
        // Stop-Process, etc.) and flush in-memory settings + active profile
        // to disk so recent mutations survive a graceful stop.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var launcher = app.Services.GetRequiredService<PanelKioskLauncher>();
            launcher.Close();
            try { app.Services.GetRequiredService<IConfigStore>().FlushNow(); }
            catch { /* best-effort */ }
            try { app.Services.GetRequiredService<ProfileManager>().SaveActiveProfile(); }
            catch { /* best-effort */ }
        });

        // Auto-install the bundled PawnIO kernel driver if not already
        // installed. Fire-and-forget; triggers a UAC prompt at first launch
        // only. Subsequent launches detect the registered service and skip.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Nexus.Service.Lifecycle.PawnIoInstaller.EnsureInstalledAsync();
                Console.Error.WriteLine($"[pawnio] driver state: {result}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pawnio] install check failed: {ex.Message}");
            }
        });
    }
}
