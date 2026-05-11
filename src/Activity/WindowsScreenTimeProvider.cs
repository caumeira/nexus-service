using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Qos.Service.Activity.Storage;
using Qos.Service.Models.Activity;
using Qos.Service.Persistence;
using Qos.Service.Platform;

namespace Qos.Service.Activity;

public sealed class WindowsScreenTimeProvider : IScreenTimeProvider, IDisposable
{
    private const long IdleThresholdMs = 3 * 60 * 1000;

    private readonly IScreenTimeStore _store;
    private readonly IConfigStore _config;
    private readonly JitteredPeriodicTimer _timer;
    private readonly object _lock = new();

    private string _currentApp = "";
    private int _currentPid;
    private long _sessionStartUtcMs;
    private long _lastPollUtcMs;

    public WindowsScreenTimeProvider(IScreenTimeStore store, IConfigStore config)
    {
        _store = store;
        _config = config;
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
        lock (_lock)
        {
            var now = NowUtcMs();
            var idleGap = now - _lastPollUtcMs;

            if (pid != _currentPid && !string.IsNullOrEmpty(appName))
            {
                if (!string.IsNullOrEmpty(_currentApp))
                {
                    var endUtc = idleGap > IdleThresholdMs ? _lastPollUtcMs : now;
                    TryRecord(_currentApp, _sessionStartUtcMs, endUtc);
                }
                _currentApp = appName;
                _currentPid = pid;
                _sessionStartUtcMs = now;
            }
            else if (idleGap > IdleThresholdMs && !string.IsNullOrEmpty(_currentApp))
            {
                TryRecord(_currentApp, _sessionStartUtcMs, _lastPollUtcMs);
                _sessionStartUtcMs = now;
            }

            _lastPollUtcMs = now;
        }
    }

    public FocusSession? GetCurrentSession()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp)) return null;
            var elapsed = TimeSpan.FromMilliseconds(NowUtcMs() - _sessionStartUtcMs);
            return new FocusSession
            {
                Id = _currentPid.ToString(),
                Name = _currentApp,
                Today = ToDuration(elapsed),
            };
        }
    }

    public IReadOnlyList<AppUsage> GetTodayUsage()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var committed = _store.GetTodayUsage(today);
        return MergeOngoing(committed);
    }

    private List<AppUsage> MergeOngoing(IReadOnlyList<AppUsage> committed)
    {
        lock (_lock)
        {
            var result = new List<AppUsage>(committed.Select(a => new AppUsage { Name = a.Name, TotalMs = a.TotalMs }));
            if (string.IsNullOrEmpty(_currentApp)) return result;

            var elapsed = NowUtcMs() - _sessionStartUtcMs;
            if (elapsed <= 0) return result;

            var existing = result.FirstOrDefault(a => a.Name == _currentApp);
            if (existing is not null)
            {
                existing.TotalMs += elapsed;
            }
            else
            {
                result.Add(new AppUsage { Name = _currentApp, TotalMs = elapsed });
            }
            return result.OrderByDescending(a => a.TotalMs).ToList();
        }
    }

    private void TryRecord(string app, long startUtc, long endUtc)
    {
        if (!IsTrackingEnabled()) return;
        _store.RecordSession(app, null, startUtc, endUtc);
    }

    private bool IsTrackingEnabled()
    {
        try { return _config.Load().ScreenTime?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_currentApp))
            {
                TryRecord(_currentApp, _sessionStartUtcMs, NowUtcMs());
            }
        }
    }

    private static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Duration ToDuration(TimeSpan ts) => new()
    {
        Total = (long)ts.TotalMilliseconds,
        Milliseconds = ts.Milliseconds,
        Seconds = ts.Seconds,
        Minutes = ts.Minutes,
        Hours = ts.Hours,
        Days = ts.Days,
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
