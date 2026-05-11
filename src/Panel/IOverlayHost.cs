namespace Qos.Service.Panel;

/// <summary>
/// Lifecycle abstraction for the floating desktop widget host. Two impls:
/// <c>PanelOverlayHostLauncher</c> on Windows (spawns qos-overlay.exe
/// out-of-proc; the overlay is a raw Win32 + direct-WebView2 P/Invoke AOT
/// binary, isolated from the service process so a WebView2 crash cannot
/// take the service down), <c>MacOverlayHost</c> on macOS (in-process
/// AppKit + WKWebView, no second binary needed because the service is
/// already a .app and the mac publish is non-AOT). Linux gets
/// <c>NoopOverlayHost</c>. Callers in Program.cs go through this interface
/// so the reconcile loop is platform-agnostic.
/// </summary>
public interface IOverlayHost
{
    bool Start();
    void Stop();
    bool IsRunning { get; }

    /// <summary>
    /// Hook for the service to push the always-on-top toggle to the host.
    /// Currently a no-op on both impls: the SPA's context menu posts the
    /// change via its own webMessage bridge inside each host, so the
    /// service doesn't need to route it. Kept on the interface for future
    /// callers that want to flip the level without an SPA round trip.
    /// </summary>
    void SetAlwaysOnTop(bool value);
}

/// <summary>Linux/unsupported fallback. Desktop widgets never start.</summary>
public sealed class NoopOverlayHost : IOverlayHost
{
    public bool IsRunning => false;
    public bool Start() => false;
    public void Stop() { }
    public void SetAlwaysOnTop(bool value) { }
}
