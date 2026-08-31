using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.FocusModes;

/// <summary>One game keeping the game trigger firing; StartTimeUtc disambiguates a reused pid, null when it could not be read.</summary>
public sealed record TrackedGame(
    string GameKey, string Name, int Pid, long StartedUtcMs, DateTime? StartTimeUtc);

/// <summary>
/// Resolves which focus mode, if any, is currently active.
///
/// At most one at a time: a manually picked mode wins, otherwise the first
/// mode in settings order whose trigger is firing, so list order is the user's
/// precedence control between (say) a game and a stream.
///
/// The game trigger tracks the game's PROCESS, not the fps focus session that
/// reports it: that session ends on the first alt-tab, so following it would
/// toggle every effect each time the player switches away.
/// </summary>
public sealed class FocusModeState : BackgroundService
{
    /// <summary>How long a candidate process must stay alive before it fires the
    /// game trigger. GameCatalog resolves by install-dir prefix, so a game's
    /// launcher, updater or shutdown handler resolves to the same game; without
    /// this, one of those taking focus after a quit re-arms the mode seconds
    /// after it correctly turned off (measured on T1: an 18s sibling process).</summary>
    private static readonly TimeSpan DefaultActivationDelay = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on one auto activation, so a trigger that never clears
    /// cannot silence notifications and egress forever. Manual is uncapped.</summary>
    private static readonly TimeSpan MaxAutoActive = TimeSpan.FromHours(12);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IConfigStore _config;
    private readonly TimeSpan _activationDelay;
    private readonly Func<TrackedGame, bool> _isRunning;

    private readonly object _lock = new();
    private readonly Dictionary<int, TrackedGame> _games = new();
    private readonly Dictionary<int, TrackedGame> _pending = new();
    private readonly Dictionary<string, long> _graceUntilUtcMs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wasFiring = new(StringComparer.Ordinal);

    private bool _obsLive;
    private string? _manualModeId;
    // Set when the user picks "Off" while a mode was auto-active: that mode
    // stays out until its trigger stops firing, so "not now" does not mean
    // "never again" and does not fight a still-running game.
    private string? _suppressedModeId;
    private string? _activeModeId;
    private string _reason = "";
    private long _activatedUtcMs;

    public FocusModeState(IConfigStore config) : this(config, DefaultActivationDelay) { }

    internal FocusModeState(
        IConfigStore config, TimeSpan activationDelay, Func<TrackedGame, bool>? isRunning = null)
    {
        _config = config;
        _activationDelay = activationDelay;
        _isRunning = isRunning ?? IsStillRunning;
    }

    /// <summary>Raised outside the state lock whenever the active mode changes; null means nothing is active.</summary>
    public event Action<FocusModeSettings?>? ActiveChanged;

    public bool IsActive { get { lock (_lock) { return _activeModeId is not null; } } }

    public string? ActiveModeId { get { lock (_lock) { return _activeModeId; } } }

    /// <summary>"auto", "manual", or "" while inactive.</summary>
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
            if (_games.ContainsKey(pid) || _pending.ContainsKey(pid)) return;
            _pending[pid] = new TrackedGame(
                gameKey, name, pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), startTime);
        }
    }

    /// <summary>Reports whether OBS is streaming or recording; drives the obs trigger.</summary>
    public void SetObsLive(bool live)
    {
        lock (_lock)
        {
            if (_obsLive == live) return;
            _obsLive = live;
        }
        Reevaluate();
    }

    /// <summary>Activates one mode by id until the user turns it off.</summary>
    public void ActivateManually(string modeId)
    {
        lock (_lock)
        {
            _manualModeId = modeId;
            _suppressedModeId = null;
        }
        Reevaluate();
    }

    /// <summary>The top bar's "Off": drops a manual pick, and holds an auto-active mode out until its trigger stops firing.</summary>
    public void TurnOff()
    {
        lock (_lock)
        {
            _manualModeId = null;
            _suppressedModeId = _reason == "auto" ? _activeModeId : null;
        }
        Reevaluate();
    }

    /// <summary>Re-runs the effect handlers against the current mode, so an effect toggled mid-session takes hold now; handlers apply a desired state rather than acting on the edge.</summary>
    public void ReapplyEffects()
    {
        Reevaluate();
        var mode = ActiveMode();
        if (mode is not null) ActiveChanged?.Invoke(mode);
    }

    /// <summary>The active mode's settings, or null when nothing is active.</summary>
    public FocusModeSettings? ActiveMode()
    {
        string? id;
        lock (_lock) { id = _activeModeId; }
        if (id is null) return null;
        return LoadSettings().Modes.FirstOrDefault(m => m.Id == id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Sweep(); }
                catch (Exception ex) { ServiceLog.Warn($"[focus] sweep failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Sweep()
    {
        lock (_lock)
        {
            PromotePendingLocked();

            List<int>? dead = null;
            foreach (var game in _games.Values)
            {
                if (!_isRunning(game)) (dead ??= new List<int>()).Add(game.Pid);
            }
            if (dead is not null)
            {
                foreach (var pid in dead) _games.Remove(pid);
            }
        }

        Reevaluate();
    }

    /// <summary>Moves candidates that have outlived the activation delay into the tracked set and drops the ones that died first.</summary>
    private void PromotePendingLocked()
    {
        if (_pending.Count == 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<int>? settled = null;
        foreach (var candidate in _pending.Values)
        {
            if (!_isRunning(candidate))
            {
                (settled ??= new List<int>()).Add(candidate.Pid);
                continue;
            }
            if (nowMs - candidate.StartedUtcMs < (long)_activationDelay.TotalMilliseconds) continue;

            (settled ??= new List<int>()).Add(candidate.Pid);
            _games[candidate.Pid] = candidate;
        }

        if (settled is not null)
        {
            foreach (var pid in settled) _pending.Remove(pid);
        }
    }

    /// <summary>Picks the active mode and fires <see cref="ActiveChanged"/> outside the lock; only a change raises it.</summary>
    private void Reevaluate()
    {
        var settings = LoadSettings();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FocusModeSettings? became = null;
        var changed = false;

        lock (_lock)
        {
            UpdateGraceWindowsLocked(settings, nowMs);

            FocusModeSettings? chosen = null;
            var reason = "";

            if (_manualModeId is not null)
            {
                chosen = settings.Modes.FirstOrDefault(m => m.Id == _manualModeId);
                if (chosen is null) _manualModeId = null;
                else reason = "manual";
            }

            if (chosen is null)
            {
                foreach (var mode in settings.Modes)
                {
                    if (mode.Id == _suppressedModeId) continue;
                    if (!IsFiringLocked(mode, nowMs)) continue;
                    chosen = mode;
                    reason = "auto";
                    break;
                }
            }

            if (chosen is not null && reason == "auto" && chosen.Id == _activeModeId
                && _activatedUtcMs > 0 && nowMs - _activatedUtcMs > (long)MaxAutoActive.TotalMilliseconds)
            {
                ServiceLog.Warn($"[focus] '{chosen.Name}' exceeded {MaxAutoActive.TotalHours:F0}h, forcing exit");
                _games.Clear();
                _pending.Clear();
                _graceUntilUtcMs.Clear();
                chosen = null;
                reason = "";
            }

            if (chosen?.Id != _activeModeId)
            {
                _activeModeId = chosen?.Id;
                _activatedUtcMs = chosen is null ? 0 : nowMs;
                became = chosen;
                changed = true;
            }
            _reason = chosen is null ? "" : reason;
        }

        if (changed)
        {
            ServiceLog.Info(became is null
                ? "[focus] inactive"
                : $"[focus] active: {became.Name} ({Reason}){FormatGames()}");
            ActiveChanged?.Invoke(became);
        }
    }

    /// <summary>Opens a mode's grace window on the tick its raw trigger stops firing, and clears a suppression once the trigger it was hiding from is gone.</summary>
    private void UpdateGraceWindowsLocked(FocusSettings settings, long nowMs)
    {
        foreach (var mode in settings.Modes)
        {
            var raw = IsTriggerRawLocked(mode);
            if (raw)
            {
                _wasFiring.Add(mode.Id);
                _graceUntilUtcMs.Remove(mode.Id);
            }
            else if (_wasFiring.Remove(mode.Id))
            {
                _graceUntilUtcMs[mode.Id] = nowMs + Math.Max(0, mode.ExitGraceSeconds) * 1000L;
            }

            if (!raw && mode.Id == _suppressedModeId) _suppressedModeId = null;
        }
    }

    private bool IsFiringLocked(FocusModeSettings mode, long nowMs)
    {
        if (IsTriggerRawLocked(mode)) return true;
        return _graceUntilUtcMs.TryGetValue(mode.Id, out var until) && nowMs < until;
    }

    private bool IsTriggerRawLocked(FocusModeSettings mode) => mode.Trigger switch
    {
        FocusTriggers.Game => _games.Count > 0,
        FocusTriggers.Obs => _obsLive,
        _ => false,
    };

    private string FormatGames()
    {
        var games = Games;
        return games.Count == 0 ? "" : ": " + string.Join(", ", games.Select(g => $"{g.Name} (pid {g.Pid})"));
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

    private FocusSettings LoadSettings()
    {
        try { return _config.Load().Focus ?? new FocusSettings(); }
        catch { return new FocusSettings(); }
    }

    /// <summary>Test hook: drives one sweep without waiting for the timer.</summary>
    internal void SweepForTests() => Sweep();
}
