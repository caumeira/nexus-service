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
/// Sends only on change, plus once on reconnect (the service's in-memory
/// snapshot resets across a helper restart).
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
        _pinnedEnumProc = (hwnd, _) =>
        {
            var owner = GetWindow(hwnd, GwOwner);
            if (WindowClassification.IsCountableWindow(IsWindowVisible(hwnd), owner))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0) pids.Add((int)pid);
            }
            return true;
        };
        EnumWindows(_pinnedEnumProc, IntPtr.Zero);
        return pids;
    }

    public void Dispose() => _timer.Dispose();

    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
#endif
