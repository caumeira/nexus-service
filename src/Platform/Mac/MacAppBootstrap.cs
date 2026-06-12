using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Mac;

// macOS startup. NSStatusItem + NSWindow must run on the main thread, so the
// caller starts the web host async (on background threads) and we initialize
// + run the AppKit run loop on the main thread synchronously. RunLoop()
// returns only when the user picks Quit from the status menu.
internal static class MacAppBootstrap
{
    private static string AbbreviateHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    public static int Run(WebApplication app, int servicePort)
    {
        // Auto-open the dashboard window when the .app finishes launching, so
        // double-clicking Nexus.app behaves like the Windows tray launch:
        // user always sees a window, not just a hidden menu-bar agent.
        // MacAppWindow hosts a WKWebView in-process; the chromeless window
        // is built from AppKit + WebKit, no Chrome / Edge dependency.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try { MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)); }
            catch (Exception ex) { Console.Error.WriteLine($"[nexus-service] mac auto-open failed: {ex.Message}"); }
        });

        // Start web host on background thread — returns immediately.
        var webTask = app.RunAsync();

        var store = app.Services.GetRequiredService<IConfigStore>();
        var showIcon = store.Load().Monitoring.ShowMacStatusBarIcon;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "status-icon.png");

        // Transfer landed with no dashboard subscribed to the WS toast — the
        // mac analog of the Windows tray balloon. Request notification
        // permission once at startup, not on the first transfer.
        MacNotify.RequestAuthorization();
        var inbox = app.Services.GetRequiredService<Nexus.Service.Transfer.TransferInbox>();
        inbox.TransferNeedsAttention += notice =>
        {
            try
            {
                var body = notice.FolderPath is { } folder
                    ? $"{notice.Text} Saved to {AbbreviateHome(folder)}."
                    : notice.Text;
                MacNotify.Send(notice.Title, body);
            }
            catch { /* best-effort */ }
        };

        MacStatusBar.Initialize(
            iconPath,
            onOpenDashboard: () => MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)),
            onOpenSettings: () => MacAppWindow.OpenOrFocus($"http://localhost:{servicePort}/system/settings"),
            onQuit: () =>
            {
                Console.WriteLine("[nexus-service] quit requested from status bar");
                _ = app.StopAsync();
                MacStatusBar.StopRunLoop();
            },
            // LaunchServices delivers kAEReopenApplication when the user
            // re-launches Nexus.app while it's already running, or clicks the
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
