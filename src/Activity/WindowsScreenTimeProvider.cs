using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Helper;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Activity;

/// <summary>
/// Windows screen-time provider. Foreground-window polling lives in the
/// user-session helper (LocalSystem in Session 0 can't see the interactive
/// desktop), and the helper sends envelopes here:
///   - <c>screenTime.session</c> -> append via IScreenTimeStore.
///   - <c>screenTime.focus</c>   -> update the in-memory current session.
/// Queries (GetCurrentSession / GetTodayUsage) read from the in-memory
/// focus snapshot and the store; they are unaffected by helper connectivity
/// (a stale current session falls off when the helper sends focus="" or
/// the session ages past tracking horizons).
/// </summary>
public sealed class WindowsScreenTimeProvider : IScreenTimeProvider, IFocusDetailsProvider, IDisposable
{
    private readonly IScreenTimeStore _store;
    private readonly IConfigStore _config;
    private readonly HelperRegistry _helper;
    private readonly object _lock = new();

    private string _currentApp = "";
    private int _currentPid;
    private long _sessionStartUtcMs;
    private string? _currentExePath;
    private int _currentWinW;
    private int _currentWinH;
    private string? _currentMonitorDevice;

    public event Action? FocusChanged;
    public event Action<FocusSessionEnded>? SessionEnded;

    public WindowsScreenTimeProvider(IScreenTimeStore store, IConfigStore config, HelperRegistry helper)
    {
        _store = store;
        _config = config;
        _helper = helper;
        _helper.InboundEnvelope += OnEnvelope;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        try
        {
            switch (env.Type)
            {
                case "screenTime.session":
                    {
                        if (env.Payload is null) return;
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenTimeSessionPayload);
                        if (p is null) return;
                        // The payload carries no pid: the helper sends this
                        // envelope before the matching "screenTime.focus" for
                        // a pid change (or, for an idle split, with no
                        // accompanying focus envelope at all), so _currentPid
                        // still holds the ending session's pid at this point.
                        int endedPid;
                        lock (_lock) { endedPid = _currentPid; }
                        SessionEnded?.Invoke(new FocusSessionEnded(endedPid, p.App, p.StartedUtcMs, p.EndedUtcMs));
                        if (!IsTrackingEnabled()) return;
                        _store.RecordSession(p.App, null, p.StartedUtcMs, p.EndedUtcMs);
                        break;
                    }
                case "screenTime.focus":
                    {
                        if (env.Payload is null) return;
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenTimeFocusPayload);
                        if (p is null) return;
                        bool changed;
                        lock (_lock)
                        {
                            changed = _currentApp != p.App || _currentPid != p.Pid;
                            _currentApp = p.App;
                            _currentPid = p.Pid;
                            _sessionStartUtcMs = p.StartedUtcMs;
                            _currentExePath = p.ExePath;
                            _currentWinW = p.WinW;
                            _currentWinH = p.WinH;
                            _currentMonitorDevice = p.MonitorDevice;
                        }
                        if (changed) FocusChanged?.Invoke();
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-time] envelope handling failed: {ex.Message}");
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

    public FocusDetails? GetCurrentFocusDetails()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp)) return null;
            return new FocusDetails(
                _currentPid, _currentApp, _sessionStartUtcMs, _currentExePath, _currentWinW, _currentWinH, _currentMonitorDevice);
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

    private bool IsTrackingEnabled()
    {
        try { return _config.Load().ScreenTime?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    public void Dispose()
    {
        _helper.InboundEnvelope -= OnEnvelope;
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
}
