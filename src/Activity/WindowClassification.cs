using System;

namespace Nexus.Service.Activity;

/// <summary>
/// The "does this window count as an app window" predicate shared by the
/// Windows user-session helper's EnumWindows callback (WindowSetPoller) and
/// its tests. Kept platform-neutral (no P/Invoke) so the classification
/// itself is testable without a live Win32 call: visible and top-level (no
/// owner), mirroring the heuristic .NET's own Process.MainWindowHandle uses.
/// </summary>
public static class WindowClassification
{
    public static bool IsCountableWindow(bool isVisible, IntPtr owner) => isVisible && owner == IntPtr.Zero;
}
