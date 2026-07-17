using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Always-on background sampler: reads one MetricSample per tick into
/// MetricsSampleBuffer, flushes the buffered tail to IMetricsHistoryStore
/// every MetricsHistory.FlushSeconds ticks, and drives TemperatureRollup's
/// bucketed Tick on the same cadence - TemperatureRollup itself runs no
/// background loop of its own. PeriodicTimer do/while shape with a per-tick
/// try/catch.
/// </summary>
public sealed class MetricsSampler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    // Bounds the wait for ISensorProvider.ReadyAsync so a platform whose
    // hardware enumeration never signals ready still starts sampling.
    private static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly ISensorProvider _sensors;
    private readonly IMetricsSource _source;
    private readonly MetricsSampleBuffer _buffer;
    private readonly IMetricsHistoryStore _store;
    private readonly TemperatureRollup _rollup;
    private readonly IAppUsageSource _appSource;
    private readonly AppSampleBuffer _appBuffer;
    private readonly IAppUsageHistoryStore _appStore;

    private int _tickCount;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastWarnUtc = DateTime.MinValue;

    public MetricsSampler(
        ISensorProvider sensors, IMetricsSource source, MetricsSampleBuffer buffer,
        IMetricsHistoryStore store, TemperatureRollup rollup,
        IAppUsageSource appSource, AppSampleBuffer appBuffer, IAppUsageHistoryStore appStore)
    {
        _sensors = sensors;
        _source = source;
        _buffer = buffer;
        _store = store;
        _rollup = rollup;
        _appSource = appSource;
        _appBuffer = appBuffer;
        _appStore = appStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _sensors.ReadyAsync(stoppingToken).WaitAsync(StartupReadyTimeout, stoppingToken).ConfigureAwait(false);
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

        _rollup.RunStartupMigration();

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                await Tick(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MaybeWarn(ex);
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));

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

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
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

            try
            {
                _rollup.Tick(nowUtc);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[metrics-sampler] rollup tick failed: {ex.Message}");
            }
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
