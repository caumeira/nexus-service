using System;
using System.Runtime.InteropServices;

namespace Qos.Service.Panel;

/// <summary>
/// Thin shim around the qos-overlay process's panel-kiosk window. The actual
/// fullscreen WebView2 lives in qos-overlay (Session 2, user context); this
/// class just posts <c>Qos.Overlay.ShowPanelKiosk</c> /
/// <c>Qos.Overlay.HidePanelKiosk</c> registered window messages to the
/// overlay's marshaler via the shared <see cref="Platform.Windows.TrayIcon.TryPostToOverlayMarshaler"/>
/// helper. The overlay itself runs the panel-display match and owns the
/// kiosk window's lifecycle.
/// </summary>
public sealed class PanelKioskLauncher
{
    // Must match the qos-overlay registered-window-message names in
    // qos-overlay/src/Program.cs (ShowPanelKioskMessageName / HidePanelKioskMessageName).
    private const string ShowPanelKioskMessageName = "Qos.Overlay.ShowPanelKiosk";
    private const string HidePanelKioskMessageName = "Qos.Overlay.HidePanelKiosk";
    private const string PanelKioskClassName = "Qos.Overlay.PanelKiosk";

    /// <summary>
    /// Best-effort check via <c>FindWindow("Qos.Overlay.PanelKiosk", null)</c>.
    /// From a Session 0 caller (LocalSystem service) this is a false-negative
    /// because the kiosk window lives in the user session and isn't visible to
    /// a service-mode FindWindow. Callers in the user session (tray, dashboard
    /// API) see the right value.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            return FindWindowW(PanelKioskClassName, null) != IntPtr.Zero;
        }
    }

    /// <summary>
    /// Ask the overlay to open the panel kiosk window. No-op if the overlay
    /// process isn't running — the overlay's startup auto-launch path (gated
    /// on the <c>panelAutoLaunch</c> pref) is the normal way the kiosk
    /// appears; this method exists for explicit triggers from the tray.
    /// </summary>
    public bool Launch()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        return Platform.Windows.TrayIcon.TryPostToOverlayMarshaler(ShowPanelKioskMessageName);
    }

    public void Close()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        Platform.Windows.TrayIcon.TryPostToOverlayMarshaler(HidePanelKioskMessageName);
    }

    /// <summary>No-op; legacy msedge orphans no longer exist.</summary>
    public static void CleanupOrphans() { }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);
}
