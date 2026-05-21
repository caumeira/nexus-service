using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Qos.Service.Persistence;

namespace Qos.Service.Platform.Mac;

// macOS startup. NSStatusItem + NSWindow must run on the main thread, so the
// caller starts the web host async (on background threads) and we initialize
// + run the AppKit run loop on the main thread synchronously. RunLoop()
// returns only when the user picks Quit from the status menu.
internal static class MacAppBootstrap
{
    public static int Run(WebApplication app, int servicePort)
    {
        // Auto-open the dashboard window when the .app finishes launching, so
        // double-clicking Qos.app behaves like the Windows tray launch:
        // user always sees a window, not just a hidden menu-bar agent.
        // MacAppWindow hosts a WKWebView in-process; the chromeless window
        // is built from AppKit + WebKit, no Chrome / Edge dependency.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try { MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)); }
            catch (Exception ex) { Console.Error.WriteLine($"[qos-service] mac auto-open failed: {ex.Message}"); }
        });

        // Start web host on background thread — returns immediately.
        var webTask = app.RunAsync();

        var store = app.Services.GetRequiredService<IConfigStore>();
        var showIcon = store.Load().Monitoring.ShowMacStatusBarIcon;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "status-icon.png");

        MacStatusBar.Initialize(
            iconPath,
            onOpenDashboard: () => MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)),
            onOpenSettings: () => MacAppWindow.OpenOrFocus($"http://localhost:{servicePort}/my-computer/settings"),
            onQuit: () =>
            {
                Console.WriteLine("[qos-service] quit requested from status bar");
                _ = app.StopAsync();
                MacStatusBar.StopRunLoop();
            },
            // LaunchServices delivers kAEReopenApplication when the user
            // re-launches Qos.app while it's already running, or clicks the
            // Dock icon. Bring the existing window forward without reloading
            // - if the user is mid-navigation, a Dock click must not refresh
            // them back to the start. navigateIfOpen=false makes
            // OpenOrFocus skip loadRequest: when the window already exists.
            onReopen: () => MacAppWindow.OpenOrFocus(
                ServiceLaunchIntent.LocalDashboardUrl(servicePort),
                navigateIfOpen: false));

        MacStatusBar.SetVisible(showIcon);

        store.OnChanged += () =>
        {
            try { MacStatusBar.SetVisible(store.Load().Monitoring.ShowMacStatusBarIcon); }
            catch { /* best-effort */ }
        };

        // Block main thread running CFRunLoop to pump AppKit events for the menu.
        // Exits when StopRunLoop() is called from the Quit action.
        MacStatusBar.RunLoop();

        // After the run loop exits, wait for the web host to finish shutting down.
        try { webTask.GetAwaiter().GetResult(); }
        catch { /* shutdown */ }
        return 0;
    }
}
