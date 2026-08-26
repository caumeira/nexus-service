using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Persistence;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
#endif

namespace Nexus.Service.Panel;

// Cross-platform reconcile for the nexus-overlay sidecar process. The same
// predicate runs on Windows (nexus-overlay.exe hosting widgets + dashboard +
// panel kiosk) and macOS (nexus-overlay-helper Swift sidecar for widgets).
//
// Run iff (OverlayWidgetsEnabled && layout.Count > 0) || Panel.AutoLaunch.
// On Mac the helper is killed on the opposite edge (no internal teardown
// path); on Windows the overlay process self-exits once both widgets and
// dashboard are gone.
internal static class OverlayHostBootstrap
{
    public static void Wire(WebApplication app)
    {
#if WINDOWS
        // Off the startup thread: one schtasks.exe per stale task, and 77 were
        // observed on a lab box.
        System.Threading.Tasks.Task.Run(PanelOverlayHostLauncher.SweepStaleLaunchTasks);
#endif
        var overlayHost = app.Services.GetRequiredService<IOverlayHost>();
        var store = app.Services.GetRequiredService<IConfigStore>();
        var panelKioskLauncher = app.Services.GetRequiredService<PanelKioskLauncher>();
        // Lock guards the edge-detect against concurrent IConfigStore.Update()
        // callers - without it two writers racing into OnChanged could both
        // pass the equality check on a stale lastShowPanel and double-
        // Launch/Close.
        var showPanelEdgeLock = new object();
        var lastShowPanel = store.Load().Panel.AutoLaunch;
        var lastAssignments = AssignmentsSignature(store.Load());
#if WINDOWS
        var helperRegistry = app.Services.GetService<HelperRegistry>();
#endif
#if LINUX
        var linuxKiosks = app.Services.GetService<Platform.Linux.LinuxPanelKioskHost>();
#endif
        store.OnChanged += () =>
        {
            try
            {
                var snapshot = store.Load();
                var shouldRun = (snapshot.Overlay.Enabled && snapshot.Overlay.Layout.Count > 0)
                                || snapshot.Panel.AutoLaunch
                                || HasMonitorPanelAssignment(snapshot);
                if (shouldRun && !overlayHost.IsRunning)
                {
                    overlayHost.Start();
                }
#if !WINDOWS
                else if (!shouldRun && overlayHost.IsRunning)
                {
                    // Mac / other: helper renders only widgets and has no
                    // internal teardown path - killing it is the canonical
                    // way to remove widgets when the layout drops to zero.
                    // Windows skips: the overlay process tears down widget
                    // HWNDs in-process via its own prefs poll, then idle-
                    // exits once both widgets and dashboard are gone.
                    overlayHost.Stop();
                }
#endif
#if WINDOWS
                // Push-notify the Windows overlay so it repolls prefs
                // immediately instead of waiting for its 5 s timer.
                try
                {
                    if (helperRegistry is not null)
                        _ = LifecycleCommands.NotifyOverlayPrefsChangedAsync(helperRegistry);
                }
                catch { }
#endif
                // Mirror Panel.AutoLaunch edge-changes onto the nexus-overlay
                // kiosk window. Compare-and-swap under the lock so concurrent
                // store.Update() writers can't both pass the edge-check on a
                // stale lastShowPanel and double-fire Launch/Close.
                bool edgeFired = false;
                bool target = false;
                lock (showPanelEdgeLock)
                {
                    if (snapshot.Panel.AutoLaunch != lastShowPanel)
                    {
                        lastShowPanel = snapshot.Panel.AutoLaunch;
                        edgeFired = true;
                        target = snapshot.Panel.AutoLaunch;
                    }
                }
                if (edgeFired)
                {
                    if (target) panelKioskLauncher.Launch();
                    else panelKioskLauncher.Close();
                }

                // Poke the host when the active assignment set changed so its
                // kiosk reconcile runs immediately (macOS stdin push; no-op on
                // Windows where the PrefsChanged push above covers it).
                var assignments = AssignmentsSignature(snapshot);
                bool assignmentsChanged;
                lock (showPanelEdgeLock)
                {
                    assignmentsChanged = assignments != lastAssignments;
                    lastAssignments = assignments;
                }
                if (assignmentsChanged)
                {
                    overlayHost.NotifyDisplayAssignmentsChanged();
#if LINUX
                    linuxKiosks?.Reconcile();
#endif
                }
            }
            catch { /* best-effort */ }
        };

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var initial = store.Load();
            var shouldStartHost = (initial.Overlay.Enabled && initial.Overlay.Layout.Count > 0)
                                  || initial.Panel.AutoLaunch
                                  || HasMonitorPanelAssignment(initial);
            if (shouldStartHost)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    overlayHost.Start();
                });
            }
#if LINUX
            // Off the startup critical path; the kiosk browser spawn is slow
            // and the session env may still be settling right after boot.
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                linuxKiosks?.Reconcile();
            });
#endif
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { overlayHost.Stop(); } catch { /* best-effort */ }
        });
    }

    // Promoted-monitor kiosks are hosted by the Windows overlay and the macOS
    // overlay-helper (both reconcile against /displays/assignments), so an
    // assignment alone must keep the host process alive. Linux kiosks are
    // spawned per-assignment by LinuxPanelKioskHost, not by IOverlayHost.
    private static bool HasMonitorPanelAssignment(NexusSettings snapshot)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return false;
        foreach (var record in snapshot.PanelDevices.Values)
        {
            // Only panels that are turned ON need the overlay; keeping it
            // alive for disabled records would resurrect a stopped panel.
            if (!string.IsNullOrEmpty(record.DisplayId) && record.Enabled != false)
                return true;
        }
        return false;
    }

    // Order-independent fingerprint of the active assignment set; a change
    // means kiosk windows must be reconciled.
    private static string AssignmentsSignature(NexusSettings snapshot)
    {
        var parts = new List<string>();
        foreach (var record in snapshot.PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && record.Enabled != false)
                parts.Add($"{record.DisplayId}|{record.Id}|{record.ReserveMonitor ?? true}");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}
