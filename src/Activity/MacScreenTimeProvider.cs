using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Activity;

public sealed class MacScreenTimeProvider : IScreenTimeProvider, IDisposable
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

    public MacScreenTimeProvider(IScreenTimeStore store, IConfigStore config)
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
            var asn = ShellOut("/usr/bin/lsappinfo", "front");
            if (string.IsNullOrEmpty(asn))
            {
                return;
            }

            var info = ShellOut("/usr/bin/lsappinfo", "info", "-only", "LSDisplayName", "-only", "pid", asn);
            if (string.IsNullOrEmpty(info))
            {
                return;
            }

            var nameMatch = Regex.Match(info, "\"LSDisplayName\"=\"(.+?)\"");
            var pidMatch = Regex.Match(info, "\"pid\"=(\\d+)");

            if (!nameMatch.Success || !pidMatch.Success)
            {
                return;
            }

            var appName = nameMatch.Groups[1].Value;
            var pid = int.Parse(pidMatch.Groups[1].Value);

            ApplyFocus(appName, pid);
        }
        catch { }
    }

    public event Action? FocusChanged;

    private void ApplyFocus(string appName, int pid)
    {
        var changed = false;
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
                changed = true;
            }
            else if (idleGap > IdleThresholdMs && !string.IsNullOrEmpty(_currentApp))
            {
                TryRecord(_currentApp, _sessionStartUtcMs, _lastPollUtcMs);
                _sessionStartUtcMs = now;
            }

            _lastPollUtcMs = now;
        }

        // Outside the lock: a subscriber activating a preset must not run
        // under the focus lock.
        if (changed) FocusChanged?.Invoke();
    }

    public FocusSession? GetCurrentSession()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp))
            {
                return null;
            }

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
            if (string.IsNullOrEmpty(_currentApp))
            {
                return result;
            }

            var elapsed = NowUtcMs() - _sessionStartUtcMs;
            if (elapsed <= 0)
            {
                return result;
            }

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
        if (!IsTrackingEnabled())
        {
            return;
        }
        _store.RecordSession(app, null, startUtc, endUtc);
    }

    private bool IsTrackingEnabled()
    {
        try
        { return _config.Load().ScreenTime?.TrackingEnabled ?? true; }
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

    private static string ShellOut(string fileName, params string[] args)
        => ShellExecutor.Run(fileName, 2000, args).Trim();
}
