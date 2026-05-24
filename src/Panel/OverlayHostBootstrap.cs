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
        var overlayHost = app.Services.GetRequiredService<IOverlayHost>();
        var store = app.Services.GetRequiredService<IConfigStore>();
        var panelKioskLauncher = app.Services.GetRequiredService<PanelKioskLauncher>();
        // Lock guards the edge-detect against concurrent IConfigStore.Update()
        // callers — without it two writers racing into OnChanged could both
        // pass the equality check on a stale lastShowPanel and double-
        // Launch/Close.
        var showPanelEdgeLock = new object();
        var lastShowPanel = store.Load().Panel.AutoLaunch;
#if WINDOWS
        var helperRegistry = app.Services.GetService<HelperRegistry>();
#endif
        store.OnChanged += () =>
        {
            try
            {
                var snapshot = store.Load();
                var shouldRun = (snapshot.Overlay.Enabled && snapshot.Overlay.Layout.Count > 0)
                                || snapshot.Panel.AutoLaunch;
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
            }
            catch { /* best-effort */ }
        };

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var initial = store.Load();
            var shouldStartHost = (initial.Overlay.Enabled && initial.Overlay.Layout.Count > 0)
                                  || initial.Panel.AutoLaunch;
            if (shouldStartHost)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    overlayHost.Start();
                });
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { overlayHost.Stop(); } catch { /* best-effort */ }
        });
    }
}
