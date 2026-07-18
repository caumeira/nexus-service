using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Always-on sampler: reads one MetricSample per tick into MetricsSampleBuffer,
/// flushes the buffered tail to IMetricsHistoryStore every MetricsHistory.FlushSeconds
/// ticks. Runs on its own dedicated thread rather than the shared thread pool, so
/// the 1Hz tick and the periodic flush are never delayed by thread-pool contention
/// elsewhere in the process (queued work, pool starvation under load).
/// </summary>
public sealed class MetricsSampler : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    // Bounds the wait for ISensorProvider.ReadyAsync so a platform whose
    // hardware enumeration never signals ready still starts sampling.
    private static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromSeconds(60);

    // Bounds StopAsync so a stuck flush can't hang service shutdown; the
    // thread is a background thread and is reclaimed by the process either way.
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly ISensorProvider _sensors;
    private readonly IMetricsSource _source;
    private readonly MetricsSampleBuffer _buffer;
    private readonly IMetricsHistoryStore _store;
    private readonly IAppUsageSource _appSource;
    private readonly AppSampleBuffer _appBuffer;
    private readonly IAppUsageHistoryStore _appStore;

    private readonly CancellationTokenSource _stopCts = new();
    private Thread? _thread;

    private int _tickCount;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastWarnUtc = DateTime.MinValue;

    public MetricsSampler(
        ISensorProvider sensors, IMetricsSource source, MetricsSampleBuffer buffer,
        IMetricsHistoryStore store,
        IAppUsageSource appSource, AppSampleBuffer appBuffer, IAppUsageHistoryStore appStore)
    {
        _sensors = sensors;
        _source = source;
        _buffer = buffer;
        _store = store;
        _appSource = appSource;
        _appBuffer = appBuffer;
        _appStore = appStore;
    }

    // Test seam: set at the top of Run() from inside the dedicated thread, so
    // a lifecycle test can confirm the loop executed off the thread pool.
    internal string? RunningThreadName { get; private set; }
    internal bool? RunningThreadIsPoolThread { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "metrics-sampler",
            // AboveNormal keeps the 1Hz tick and periodic flush landing on
            // schedule when the machine is busy; not Highest, so it never
            // starves real-time work elsewhere (audio capture, the lighting
            // engine's own render thread).
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        _thread?.Join(StopJoinTimeout);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Cancel + Join unconditionally rather than gating on
        // IsCancellationRequested: if StopAsync's own Join already timed out
        // (a slow flush still in flight), this is a second bounded wait
        // window rather than an immediate disposal of _stopCts while the
        // thread can still touch its Token.
        _stopCts.Cancel();
        _thread?.Join(StopJoinTimeout);
        _stopCts.Dispose();
    }

    private void Run()
    {
        RunningThreadName = Thread.CurrentThread.Name;
        RunningThreadIsPoolThread = Thread.CurrentThread.IsThreadPoolThread;

        try
        {
            _sensors.ReadyAsync(_stopCts.Token).WaitAsync(StartupReadyTimeout, _stopCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
            // Sensor enumeration is still warming up; sample anyway on the
            // schedule below rather than block forever.
        }

        // Scheduling uses the monotonic Stopwatch clock, not DateTime, so a
        // backward wall-clock step (NTP correction, DST edge case) never
        // stalls the wait for hours - Tick still receives DateTime.UtcNow for
        // the persisted sample timestamp, unaffected by this choice.
        var intervalTicks = (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);
        var nextDeadlineTicks = Stopwatch.GetTimestamp() + intervalTicks;
        while (true)
        {
            try
            {
                Tick(DateTime.UtcNow, _stopCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MaybeWarn(ex);
            }

            nextDeadlineTicks += intervalTicks;
            var remainingTicks = nextDeadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks < 0)
            {
                // Starved past a whole interval: re-anchor instead of banking
                // debt into a burst of catch-up ticks.
                nextDeadlineTicks = Stopwatch.GetTimestamp() + intervalTicks;
                remainingTicks = intervalTicks;
            }

            // A real stop event with a computed timeout: wakes immediately on
            // Stop, otherwise paces the next tick onto the interval boundary.
            var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
            if (_stopCts.Token.WaitHandle.WaitOne(remaining))
            {
                break;
            }
        }

        // Graceful stop: flush whatever the buffer holds so a clean shutdown
        // never drops up to FlushSeconds worth of unflushed samples.
        try
        {
            Flush(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-sampler] final flush failed: {ex.Message}");
        }
    }

    internal async Task Tick(DateTime nowUtc, CancellationToken ct)
    {
        var tsSec = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var sample = await _source.SampleAsync(tsSec, ct).ConfigureAwait(false);
        _buffer.Append(sample);
        _tickCount++;

        if (_tickCount % MetricsHistory.AppSampleIntervalSeconds == 0)
        {
            // Independent from the scalar source above: an app-sampling
            // failure must never suppress the flush check below, or a
            // broken per-app read would delay the core scalar flush too.
            try
            {
                _appBuffer.Append(new AppUsageTick(tsSec, _appSource.Sample()));
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[metrics-sampler] app sample failed: {ex.Message}");
            }
        }

        if (_tickCount % MetricsHistory.FlushSeconds == 0)
        {
            Flush(nowUtc);
        }
    }

    private void Flush(DateTime nowUtc)
    {
        var pending = _buffer.PendingSnapshot();
        var isHourly = nowUtc - _lastPruneUtc >= PruneInterval;
        long? pruneCutoffSec = isHourly
            ? new DateTimeOffset(nowUtc).ToUnixTimeSeconds() - MetricsHistory.RetentionDays * 86_400L
            : null;

        if (pending.Count > 0)
        {
            try
            {
                _store.Append(pending, pruneCutoffSec);
                _buffer.RemoveThrough(pending[^1].TsSec);
                if (isHourly)
                {
                    _lastPruneUtc = nowUtc;
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[metrics-sampler] flush failed: {ex.Message}");
                _buffer.TrimToRetentionCap(new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
            }
        }

        FlushApps(nowUtc, pruneCutoffSec);
    }

    // Runs on the same flush cadence as the scalar path but independently:
    // its own try/catch, so a per-app store failure never blocks the scalar
    // RemoveThrough above (and vice versa). Called unconditionally (even
    // with an empty tail) so an hourly prune of the app tables still runs
    // on a tick where no app data happened to buffer.
    private void FlushApps(DateTime nowUtc, long? pruneCutoffSec)
    {
        var appPending = _appBuffer.PendingSnapshot();
        try
        {
            _appStore.Append(appPending, pruneCutoffSec);
            if (appPending.Count > 0)
            {
                _appBuffer.RemoveThrough(appPending[^1].TsSec);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-sampler] app flush failed: {ex.Message}");
            _appBuffer.TrimToRetentionCap(new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
        }
    }

    private void MaybeWarn(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWarnUtc < WarnThrottle)
        {
            return;
        }
        _lastWarnUtc = now;
        ServiceLog.Warn($"[metrics-sampler] tick failed: {ex.Message}");
    }
}
