using System;

namespace Nexus.Service.Activity;

/// <summary>
/// The "does this window count as a real app window" predicate shared by
/// the Windows user-session helper's EnumWindows callback (WindowSetPoller)
/// and its tests. Kept platform-neutral (no P/Invoke) - the poller extracts
/// each attribute via Win32 and this only decides. Matches Task Manager's
/// Apps definition, not .NET's own (more permissive) MainWindowHandle
/// heuristic: IsWindowVisible alone still passes DWM-cloaked windows (hidden
/// UWP/background windows, and the common case behind service processes
/// like svchost owning technically-visible windows nobody sees), so cloak
/// state is required, not optional.
/// </summary>
public static class WindowClassification
{
    public static bool IsCountableWindow(
        bool isVisible,
        IntPtr owner,
        bool isToolWindow,
        bool isCloaked,
        bool hasTitle,
        bool hasOnScreenBounds)
    {
        return isVisible
            && owner == IntPtr.Zero
            && !isToolWindow
            && !isCloaked
            && hasTitle
            && hasOnScreenBounds;
    }

    /// <summary>True when the window rect has positive area and overlaps the
    /// virtual screen (the bounding box of every monitor) - excludes
    /// zero-size and fully off-screen windows.</summary>
    public static bool HasOnScreenBounds(
        int left, int top, int right, int bottom,
        int virtualLeft, int virtualTop, int virtualRight, int virtualBottom)
    {
        if (right <= left || bottom <= top)
        {
            return false;
        }
        return left < virtualRight && right > virtualLeft && top < virtualBottom && bottom > virtualTop;
    }
}
