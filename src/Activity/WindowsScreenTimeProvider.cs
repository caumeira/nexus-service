using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Qos.Service.Activity.Storage;
using Qos.Service.Helper;
using Qos.Service.Models.Activity;
using Qos.Service.Persistence;
using Qos.Service.Serialization;

namespace Qos.Service.Activity;

/// <summary>
/// Windows screen-time provider. Foreground-window polling lives in the
/// user-session helper (LocalSystem in Session 0 can't see the interactive
/// desktop), and the helper sends envelopes here:
///   - <c>screenTime.session</c> -> append to SQLite via IScreenTimeStore.
///   - <c>screenTime.focus</c>   -> update the in-memory current session.
/// Queries (GetCurrentSession / GetTodayUsage) read from the in-memory
/// focus snapshot and the store; they are unaffected by helper connectivity
/// (a stale current session falls off when the helper sends focus="" or
/// the session ages past tracking horizons).
/// </summary>
public sealed class WindowsScreenTimeProvider : IScreenTimeProvider, IDisposable
{
    private readonly IScreenTimeStore _store;
    private readonly IConfigStore _config;
    private readonly HelperRegistry _helper;
    private readonly object _lock = new();

    private string _currentApp = "";
    private int _currentPid;
    private long _sessionStartUtcMs;

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
                        if (!IsTrackingEnabled()) return;
                        _store.RecordSession(p.App, null, p.StartedUtcMs, p.EndedUtcMs);
                        break;
                    }
                case "screenTime.focus":
                    {
                        if (env.Payload is null) return;
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ScreenTimeFocusPayload);
                        if (p is null) return;
                        lock (_lock)
                        {
                            _currentApp = p.App;
                            _currentPid = p.Pid;
                            _sessionStartUtcMs = p.StartedUtcMs;
                        }
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
