#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Qos.Service.Helper.Domains;
using Qos.Service.Platform;
using Qos.Service.Serialization;

namespace Qos.Service.Helper;

/// <summary>
/// Helper-side foreground-window poller. Polls GetForegroundWindow every
/// 2s (jittered), tracks the active app, and emits envelopes to the
/// service on focus boundaries:
///
///   <c>screenTime.focus</c>  - current focus changed (or no focus).
///   <c>screenTime.session</c> - the prior focus session ended; the service
///                               persists it to SQLite.
///
/// This is the user-session counterpart that replaces the in-process
/// foreground polling that was running (but blind) on the LocalSystem
/// service. The shape mirrors the original WindowsScreenTimeProvider
/// (3-minute idle clip, lock-free apply path), just with the sink swapped
/// from IScreenTimeStore.RecordSession to HelperOutbound.SendAsync.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenTimePoller : IDisposable
{
    private const long IdleThresholdMs = 3 * 60 * 1000;

    private readonly HelperOutbound _outbound;
    private readonly JitteredPeriodicTimer _timer;
    private readonly object _lock = new();

    private string _currentApp = "";
    private int _currentPid;
    private long _sessionStartUtcMs;
    private long _lastPollUtcMs;

    public ScreenTimePoller(HelperOutbound outbound)
    {
        _outbound = outbound;
        _lastPollUtcMs = NowUtcMs();
        _timer = new JitteredPeriodicTimer(periodMs: 2000, jitterMs: 200, Poll);
    }

    private void Poll()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return;

            string appName;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                appName = proc.ProcessName;
            }
            catch { return; }

            ApplyFocus(appName, (int)pid);
        }
        catch { }
    }

    private void ApplyFocus(string appName, int pid)
    {
        FireMode mode;
        string priorApp = "";
        long priorStart = 0;
        long priorEnd = 0;
        int newPid = 0;
        long newStart = 0;

        lock (_lock)
        {
            var now = NowUtcMs();
            var idleGap = now - _lastPollUtcMs;

            if (pid != _currentPid && !string.IsNullOrEmpty(appName))
            {
                priorApp = _currentApp;
                priorStart = _sessionStartUtcMs;
                priorEnd = idleGap > IdleThresholdMs ? _lastPollUtcMs : now;
                _currentApp = appName;
                _currentPid = pid;
                _sessionStartUtcMs = now;
                newPid = pid;
                newStart = now;
                mode = string.IsNullOrEmpty(priorApp) ? FireMode.FocusOnly : FireMode.SessionAndFocus;
            }
            else if (idleGap > IdleThresholdMs && !string.IsNullOrEmpty(_currentApp))
            {
                priorApp = _currentApp;
                priorStart = _sessionStartUtcMs;
                priorEnd = _lastPollUtcMs;
                _sessionStartUtcMs = now;
                newPid = _currentPid;
                newStart = now;
                mode = FireMode.SessionOnly;
            }
            else
            {
                mode = FireMode.None;
            }

            _lastPollUtcMs = now;
        }

        if (mode == FireMode.None) return;

        // Fire outside the lock - the pipe write is async and might block
        // on a flush; we don't want to hold the focus lock through that.
        if (mode is FireMode.SessionOnly or FireMode.SessionAndFocus
            && !string.IsNullOrEmpty(priorApp))
        {
            _ = _outbound.SendAsync(
                "screenTime.session",
                new ScreenTimeSessionPayload
                {
                    App = priorApp,
                    StartedUtcMs = priorStart,
                    EndedUtcMs = priorEnd,
                },
                AppJsonContext.Default.ScreenTimeSessionPayload);
        }

        if (mode is FireMode.FocusOnly or FireMode.SessionAndFocus)
        {
            _ = _outbound.SendAsync(
                "screenTime.focus",
                new ScreenTimeFocusPayload
                {
                    App = _currentApp,
                    Pid = newPid,
                    StartedUtcMs = newStart,
                },
                AppJsonContext.Default.ScreenTimeFocusPayload);
        }
    }

    private enum FireMode { None, FocusOnly, SessionOnly, SessionAndFocus }

    public void Dispose()
    {
        _timer.Dispose();

        // Flush the in-flight session on shutdown so the user doesn't lose
        // an open VSCode/Chrome session whenever they log off.
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_currentApp))
            {
                _ = _outbound.SendAsync(
                    "screenTime.session",
                    new ScreenTimeSessionPayload
                    {
                        App = _currentApp,
                        StartedUtcMs = _sessionStartUtcMs,
                        EndedUtcMs = NowUtcMs(),
                    },
                    AppJsonContext.Default.ScreenTimeSessionPayload);
            }
        }
    }

    private static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
#endif
