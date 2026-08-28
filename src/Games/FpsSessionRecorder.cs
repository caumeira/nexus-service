using System.Text;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sensors;

namespace Nexus.Service.Games;

/// <summary>
/// Follows the helper's own focus session (IScreenTimeProvider.FocusChanged /
/// IFocusDetailsProvider.SessionEnded) rather than any boundary logic of its
/// own: a session opens when focus lands on a pid whose exe resolves via
/// GameCatalog, consumes the per-second frames IFpsProvider.TryReadSecond
/// already exposes for the 1Hz history sampler, and closes on the matching
/// session-end event (pid change, idle split, or helper disconnect flush).
///
/// Windows-only in practice: registered as a hosted service only where
/// IFocusDetailsProvider is registered (WindowsScreenTimeProvider); the
/// class itself has no platform guard since every other dependency here is
/// already a cross-platform interface.
/// </summary>
public sealed class FpsSessionRecorder : IHostedService, IDisposable
{
    // Re-checks the focus monitor's display mode this often (in poll ticks),
    // not every tick - Enumerate() round-trips to the helper on Windows.
    private const int ModeCheckEveryTicks = 5;

    private readonly IFpsProvider _fps;
    private readonly IScreenTimeProvider _screenTime;
    private readonly IFocusDetailsProvider _focusDetails;
    private readonly GameCatalog _catalog;
    private readonly IConfigStore _config;
    private readonly BinaryFpsSessionStore _store;
    private readonly IDisplayTopologyProvider _displays;
    private readonly ISensorProvider _sensors;

    private readonly object _lock = new();
    private TrackedSession? _open;
    private int _modeCheckCounter;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public FpsSessionRecorder(
        IFpsProvider fps, IScreenTimeProvider screenTime, IFocusDetailsProvider focusDetails,
        GameCatalog catalog, IConfigStore config, BinaryFpsSessionStore store,
        IDisplayTopologyProvider displays, ISensorProvider sensors)
    {
        _fps = fps;
        _screenTime = screenTime;
        _focusDetails = focusDetails;
        _catalog = catalog;
        _config = config;
        _store = store;
        _displays = displays;
        _sensors = sensors;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _screenTime.FocusChanged += OnFocusChanged;
        _focusDetails.SessionEnded += OnFocusSessionEnded;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);

        // Pick up a game already focused when the service starts.
        OnFocusChanged();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _screenTime.FocusChanged -= OnFocusChanged;
        _focusDetails.SessionEnded -= OnFocusSessionEnded;
        _cts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        TrackedSession? open;
        lock (_lock) { open = _open; _open = null; }
        if (open is not null)
        {
            FinalizeAndPersist(open, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public void Dispose() => _cts?.Dispose();

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!IsTrackingEnabled())
                {
                    // "no sessions" is emphatic in the plan - a session open
                    // when tracking is switched off is discarded, not persisted.
                    lock (_lock) { _open = null; }
                    continue;
                }

                TrackedSession? open;
                lock (_lock) { open = _open; }
                if (open is null)
                {
                    continue;
                }

                var tsSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
                if (_fps.TryReadSecond(tsSec, out var pid, out var frames)
                    && pid == open.Pid && FpsSessionRules.IsValidFrameCount(frames))
                {
                    lock (_lock)
                    {
                        if (!ReferenceEquals(_open, open))
                        {
                            continue;
                        }
                        open.ValidSec++;
                        open.Frames += frames;
                        FpsHistogram.AddSample(open.Hist, frames);
                        open.MinFps = Math.Min(open.MinFps, frames);
                        open.MaxFps = Math.Max(open.MaxFps, frames);
                    }
                }

                if (++_modeCheckCounter >= ModeCheckEveryTicks)
                {
                    _modeCheckCounter = 0;
                    CheckForModeChange(open);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void OnFocusChanged()
    {
        if (!IsTrackingEnabled())
        {
            return;
        }

        var details = _focusDetails.GetCurrentFocusDetails();
        if (details is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_open is not null && _open.Pid == details.Pid && _open.StartedUtcMs == details.StartedUtcMs)
            {
                return;
            }
        }

        TryOpenForFocus(details);
    }

    private void TryOpenForFocus(FocusDetails details)
    {
        if (string.IsNullOrEmpty(details.ExePath))
        {
            _catalog.NotifyUnknownExe();
            return;
        }

        if (!_catalog.TryResolve(details.ExePath, out var identity))
        {
            _catalog.NotifyUnknownExe();
            return;
        }

        OpenSession(details, identity);
    }

    private void OpenSession(FocusDetails details, GameIdentity identity)
    {
        var (dispW, dispH, refreshHz) = ResolveDisplayMode(details.MonitorDevice);

        var open = new TrackedSession
        {
            Pid = details.Pid,
            GameKey = identity.GameKey,
            GameName = identity.Name,
            Store = identity.Store,
            StartedUtcMs = details.StartedUtcMs,
            MonitorDevice = details.MonitorDevice,
            DispW = dispW,
            DispH = dispH,
            RefreshHz = refreshHz,
            WinW = details.WinW,
            WinH = details.WinH,
            HardwareHash = ComputeHardwareHash(),
        };

        lock (_lock)
        {
            _open = open;
            _modeCheckCounter = 0;
        }
    }

    private void OnFocusSessionEnded(FocusSessionEnded ended)
    {
        TrackedSession? toClose = null;
        lock (_lock)
        {
            if (_open is not null && _open.Pid == ended.Pid && _open.StartedUtcMs == ended.StartedUtcMs)
            {
                toClose = _open;
                _open = null;
            }
        }
        if (toClose is not null)
        {
            FinalizeAndPersist(toClose, ended.EndedUtcMs);
        }
    }

    // A mode change mid-session closes and reopens per the plan's decision 5
    // (resolution/Hz are part of the signature): re-fetches current focus
    // details for the fresh window snapshot rather than reusing the stale
    // one from open.
    private void CheckForModeChange(TrackedSession open)
    {
        var (dispW, dispH, refreshHz) = ResolveDisplayMode(open.MonitorDevice);
        if (dispW == 0 && dispH == 0)
        {
            return; // topology unavailable this tick; do not thrash the session over it
        }
        if (dispW == open.DispW && dispH == open.DispH && refreshHz == open.RefreshHz)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TrackedSession? closed;
        lock (_lock)
        {
            if (!ReferenceEquals(_open, open))
            {
                return;
            }
            closed = _open;
            _open = null;
        }
        FinalizeAndPersist(closed, nowMs);

        var details = _focusDetails.GetCurrentFocusDetails();
        if (details is null || details.Pid != open.Pid)
        {
            return;
        }
        TryOpenForFocus(details);
    }

    private void FinalizeAndPersist(TrackedSession open, long endedUtcMs)
    {
        var focusedSec = (int)Math.Max(0, (endedUtcMs - open.StartedUtcMs) / 1000);
        if (focusedSec < FpsSessionRules.MinFocusedSecToPersist)
        {
            return;
        }

        var p10 = FpsHistogram.Percentile(open.Hist, 10);
        var p50 = FpsHistogram.Percentile(open.Hist, 50);
        var p90 = FpsHistogram.Percentile(open.Hist, 90);
        var capped = p90 - p10 <= FpsSessionRules.CappedSpreadFps && FpsHistogram.Total(open.Hist) > 0;

        var fullscreen = open.WinW > 0 && open.WinH > 0 && open.WinW == open.DispW && open.WinH == open.DispH;

        var record = new FpsSessionRecord(
            Guid.NewGuid(), open.GameKey, open.GameName, open.Store,
            open.StartedUtcMs, endedUtcMs, focusedSec, open.ValidSec, open.Frames,
            open.MinFps == int.MaxValue ? 0 : open.MinFps, open.MaxFps, open.Hist,
            open.DispW, open.DispH, open.RefreshHz, open.WinW, open.WinH,
            fullscreen, capped, capped ? p50 : 0, open.HardwareHash, FpsUploadState.Pending);

        try
        {
            _store.Append(record);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[fps-session] persist failed: {ex.Message}");
        }
    }

    private (int Width, int Height, int RefreshHz) ResolveDisplayMode(string? monitorDevice)
    {
        if (string.IsNullOrEmpty(monitorDevice))
        {
            return (0, 0, 0);
        }
        try
        {
            var displays = _displays.Enumerate();
            if (displays is null)
            {
                return (0, 0, 0);
            }
            foreach (var d in displays)
            {
                if (string.Equals(d.Id, monitorDevice, StringComparison.Ordinal))
                {
                    return (d.ResolutionWidth, d.ResolutionHeight, d.RefreshHz);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[fps-session] display topology query failed: {ex.Message}");
        }
        return (0, 0, 0);
    }

    private ulong ComputeHardwareHash()
    {
        try
        {
            var cpu = _sensors.GetCpuModel() ?? "";
            var gpu = Nexus.Service.Benchmarks.BenchmarkRunner.SelectReportedGpus(_sensors.GetGpus()).FirstOrDefault() ?? "";
            var mobo = _sensors.GetMotherboardModel() ?? "";
            var ramBytes = Nexus.Service.Benchmarks.BenchmarkRunner.ParseRamBytes(_sensors.GetMemoryTotalFormatted());
            return HardwareHash.Compute(cpu, gpu, mobo, ramBytes);
        }
        catch
        {
            return 0;
        }
    }

    private bool IsTrackingEnabled()
    {
        try { return _config.Load().Fps?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    private sealed class TrackedSession
    {
        public required int Pid { get; init; }
        public required string GameKey { get; init; }
        public required string GameName { get; init; }
        public required string Store { get; init; }
        public required long StartedUtcMs { get; init; }
        public string? MonitorDevice { get; init; }
        public int DispW { get; init; }
        public int DispH { get; init; }
        public int RefreshHz { get; init; }
        public int WinW { get; init; }
        public int WinH { get; init; }
        public ulong HardwareHash { get; init; }
        public int ValidSec;
        public long Frames;
        public uint[] Hist { get; } = new uint[FpsHistogram.BucketCount];
        public int MinFps = int.MaxValue;
        public int MaxFps;
    }
}

/// <summary>Deterministic 64-bit hash of a rig's stable identity fields
/// (cpu, primary gpu, motherboard, ram capacity) - not a benchmark score, per
/// the fps-benchmarks plan's decision 1. Plain FNV-1a: fast, stable across
/// runs and platforms, and the exact algorithm never leaves this box (only
/// the resulting number would, in a later upload phase).</summary>
public static class HardwareHash
{
    public static ulong Compute(string cpu, string gpu, string motherboard, long ramBytes)
    {
        var text = $"{cpu}|{gpu}|{motherboard}|{ramBytes}";
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
