using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Games;

/// <summary>One game keeping Game Mode active; StartTimeUtc disambiguates a reused pid, null when it could not be read.</summary>
public sealed record TrackedGame(
    string GameKey, string Name, int Pid, long StartedUtcMs, DateTime? StartTimeUtc);

/// <summary>
/// Resolves Game Mode activity from the manual state ("auto"/"on"/"off") and
/// the tracked games <see cref="FpsSessionRecorder"/> reports.
///
/// Tracks the game's PROCESS, not the focus session that opened it: that
/// session ends on the first alt-tab, so following it would toggle every
/// effect each time the player switches away. Sweeps on its own timer because
/// the recorder is hosted on Windows only while the manual state is not.
/// </summary>
public sealed class GameModeState : BackgroundService
{
    public const string StateAuto = "auto";
    public const string StateOn = "on";
    public const string StateOff = "off";

    /// <summary>Ceiling on one AUTO activation, so a liveness check that never fires cannot silence notifications and egress forever; a manual "on" is uncapped.</summary>
    private static readonly TimeSpan MaxAutoActive = TimeSpan.FromHours(12);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IConfigStore _config;
    private readonly object _lock = new();
    private readonly Dictionary<int, TrackedGame> _games = new();

    private bool _active;
    private string _reason = "";
    private long _activatedUtcMs;
    private long _graceUntilUtcMs;

    public GameModeState(IConfigStore config)
    {
        _config = config;
    }

    /// <summary>Raised outside the state lock on every activity flip; background workers poll <see cref="IsActive"/> instead of subscribing.</summary>
    public event Action<bool>? ActiveChanged;

    public bool IsActive { get { lock (_lock) { return _active; } } }

    /// <summary>"auto" while a tracked game is running, "manual" while the user
    /// switched it on by hand, "" while inactive.</summary>
    public string Reason { get { lock (_lock) { return _reason; } } }

    public long ActivatedUtcMs { get { lock (_lock) { return _activatedUtcMs; } } }

    public IReadOnlyList<TrackedGame> Games
    {
        get { lock (_lock) { return _games.Values.OrderBy(g => g.StartedUtcMs).ToList(); } }
    }

    /// <summary>Records a running game; re-noting a tracked pid keeps the original stamp, since a session opens again on every alt-tab back.</summary>
    public void NoteGameStarted(string gameKey, string name, int pid)
    {
        if (pid <= 0) return;

        var startTime = TryReadProcessStartUtc(pid);
        lock (_lock)
        {
            if (_games.ContainsKey(pid)) return;
            _games[pid] = new TrackedGame(
                gameKey, name, pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), startTime);
            _graceUntilUtcMs = 0;
        }
        Reevaluate();
    }

    /// <summary>Writes the manual state and re-evaluates immediately, so the
    /// route's response already reflects the new activity.</summary>
    public void SetManualState(string state)
    {
        var normalized = Normalize(state);
        _config.Update(s =>
        {
            s.GameMode ??= new GameModeSettings();
            s.GameMode.State = normalized;
        });
        Reevaluate();
    }

    public string GetManualState() => Normalize(LoadSettings().State);

    /// <summary>Re-raises the current activity so an effect toggled mid-session takes hold now; handlers must therefore apply a desired state, not act on the edge.</summary>
    public void ReapplyEffects()
    {
        Reevaluate();
        if (IsActive) ActiveChanged?.Invoke(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Sweep(); }
                catch (Exception ex) { ServiceLog.Warn($"[game-mode] sweep failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Sweep()
    {
        List<int>? dead = null;
        lock (_lock)
        {
            foreach (var game in _games.Values)
            {
                if (!IsStillRunning(game))
                {
                    dead ??= new List<int>();
                    dead.Add(game.Pid);
                }
            }

            if (dead is not null)
            {
                foreach (var pid in dead) _games.Remove(pid);
                if (_games.Count == 0 && _active)
                {
                    _graceUntilUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        + Math.Max(0, LoadSettings().ExitGraceSeconds) * 1000L;
                }
            }
        }

        Reevaluate();
    }

    /// <summary>Recomputes activity and fires <see cref="ActiveChanged"/> outside the lock; only a transition raises it.</summary>
    private void Reevaluate()
    {
        var settings = LoadSettings();
        var manual = Normalize(settings.State);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool? transition = null;

        lock (_lock)
        {
            bool wantActive;
            string reason;

            if (manual == StateOn)
            {
                wantActive = true;
                reason = "manual";
            }
            else if (manual == StateOff)
            {
                wantActive = false;
                reason = "";
            }
            else
            {
                var inGrace = _graceUntilUtcMs > 0 && nowMs < _graceUntilUtcMs;
                wantActive = _games.Count > 0 || inGrace;
                reason = "auto";

                if (wantActive && _active && _reason == "auto"
                    && _activatedUtcMs > 0
                    && nowMs - _activatedUtcMs > (long)MaxAutoActive.TotalMilliseconds)
                {
                    ServiceLog.Warn(
                        $"[game-mode] auto activation exceeded {MaxAutoActive.TotalHours:F0}h, forcing exit");
                    _games.Clear();
                    _graceUntilUtcMs = 0;
                    wantActive = false;
                }
            }

            if (!wantActive)
            {
                if (_games.Count == 0) _graceUntilUtcMs = 0;
                reason = "";
            }

            if (wantActive != _active)
            {
                _active = wantActive;
                _reason = reason;
                _activatedUtcMs = wantActive ? nowMs : 0;
                transition = wantActive;
            }
            else if (wantActive && _reason != reason)
            {
                _reason = reason;
            }
        }

        if (transition is bool became)
        {
            ServiceLog.Info(became
                ? $"[game-mode] active ({Reason}){FormatGames()}"
                : "[game-mode] inactive");
            ActiveChanged?.Invoke(became);
        }
    }

    private string FormatGames()
    {
        var games = Games;
        return games.Count == 0 ? "" : ": " + string.Join(", ", games.Select(g => g.Name));
    }

    private static bool IsStillRunning(TrackedGame game)
    {
        try
        {
            using var proc = Process.GetProcessById(game.Pid);
            if (proc.HasExited) return false;
            if (game.StartTimeUtc is null) return true;
            // A recycled pid is a different process with a later start time.
            return Math.Abs((proc.StartTime.ToUniversalTime() - game.StartTimeUtc.Value).TotalSeconds) < 1;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Only ArgumentException means "no such pid"; a process we cannot open is still alive.
            return true;
        }
    }

    private static DateTime? TryReadProcessStartUtc(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private GameModeSettings LoadSettings()
    {
        try { return _config.Load().GameMode ?? new GameModeSettings(); }
        catch { return new GameModeSettings(); }
    }

    internal static string Normalize(string? state) => state switch
    {
        StateOn => StateOn,
        StateOff => StateOff,
        _ => StateAuto,
    };

    /// <summary>Test hook: drives one sweep without waiting for the timer.</summary>
    internal void SweepForTests() => Sweep();

    /// <summary>Test hook: drops all tracked games, as a service restart would.</summary>
    internal void ResetForTests()
    {
        lock (_lock)
        {
            _games.Clear();
            _graceUntilUtcMs = 0;
            _active = false;
            _reason = "";
            _activatedUtcMs = 0;
        }
    }
}
