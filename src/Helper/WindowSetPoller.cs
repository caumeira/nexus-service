#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Activity;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side visible-window poller. Session 0 (the LocalSystem service)
/// cannot enumerate the interactive desktop's windows, so this runs here and
/// pushes the set of process ids owning a visible top-level window on a
/// jittered timer, matching ScreenTimePoller's cadence. Consumed by
/// WindowsWindowSetProvider for the processes frame's isApp classification.
/// Sends only on change, plus once on reconnect: a service restart wipes
/// WindowsWindowSetProvider's in-memory snapshot, but this poller's own
/// _lastSent survives the pipe reconnect that follows, so an unchanged
/// window set would otherwise never get resent to repopulate it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowSetPoller : IDisposable
{
    private readonly HelperOutbound _outbound;
    private readonly JitteredPeriodicTimer _timer;
    private HashSet<int> _lastSent = new();
    private bool _wasConnected;

    // Pinned callback - EnumWindows requires a delegate that isn't GC'd
    // mid-enumeration (same guard as TrayIcon._pinnedEnumProc). The timer
    // only re-arms after Poll returns, so one instance field is safe: no two
    // enumerations for this poller are ever in flight together.
    private EnumWindowsProc? _pinnedEnumProc;

    public WindowSetPoller(HelperOutbound outbound)
    {
        _outbound = outbound;
        _timer = new JitteredPeriodicTimer(periodMs: 2000, jitterMs: 200, Poll);
    }

    private void Poll()
    {
        try
        {
            var connected = _outbound.IsConnected;
            var justReconnected = connected && !_wasConnected;
            _wasConnected = connected;

            var current = SnapshotWindowedPids();
            if (!justReconnected && current.SetEquals(_lastSent)) return;

            _lastSent = current;
            _ = _outbound.SendAsync(
                "windowSet.snapshot",
                new WindowSetSnapshotPayload { Pids = current.ToList() },
                AppJsonContext.Default.WindowSetSnapshotPayload);
        }
        catch { }
    }

    private HashSet<int> SnapshotWindowedPids()
    {
        var pids = new HashSet<int>();

        // Computed once per poll, not per window: the bounding box of every
        // monitor, for the off-screen-window exclusion.
        var virtualLeft = GetSystemMetrics(SmXvirtualscreen);
        var virtualTop = GetSystemMetrics(SmYvirtualscreen);
        var virtualRight = virtualLeft + GetSystemMetrics(SmCxvirtualscreen);
        var virtualBottom = virtualTop + GetSystemMetrics(SmCyvirtualscreen);

        _pinnedEnumProc = (hwnd, _) =>
        {
            var owner = GetWindow(hwnd, GwOwner);
            var isToolWindow = (GetWindowLong(hwnd, GwlExstyle) & WsExToolwindow) != 0;
            var isCloaked = IsCloaked(hwnd);
            var hasTitle = GetWindowTextLength(hwnd) > 0;
            var hasOnScreenBounds = GetWindowRect(hwnd, out var rect) &&
                WindowClassification.HasOnScreenBounds(
                    rect.Left, rect.Top, rect.Right, rect.Bottom,
                    virtualLeft, virtualTop, virtualRight, virtualBottom);

            if (WindowClassification.IsCountableWindow(
                    IsWindowVisible(hwnd), owner, isToolWindow, isCloaked, hasTitle, hasOnScreenBounds))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0) pids.Add((int)pid);
            }
            return true;
        };
        EnumWindows(_pinnedEnumProc, IntPtr.Zero);
        return pids;
    }

    // DWMWA_CLOAKED reports non-zero for a window Explorer keeps alive but
    // never shows (background UWP frames, DWM-hidden helper windows) -
    // IsWindowVisible alone still reports these as visible.
    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    public void Dispose() => _timer.Dispose();

    private const uint GwOwner = 4;
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
#endif
