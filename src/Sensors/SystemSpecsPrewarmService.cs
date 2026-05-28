using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Sensors;

/// <summary>
/// Fills the <see cref="SystemSpecsCollector"/> cache in the background after
/// the host reports ready, so the first Devices → System Specs request hits a
/// warm cache (~5 ms) instead of paying for a cold PowerShell spawn (~400 ms
/// even with the consolidated single-call script).
///
/// Critically, this MUST NOT delay host startup. <see cref="Task.Yield"/>
/// hands control back to <c>BackgroundService.StartAsync</c> immediately so
/// the host completes its boot and <c>/ping</c> responds while the prewarm
/// continues on a thread-pool thread.
/// </summary>
public sealed class SystemSpecsPrewarmService : BackgroundService
{
    private readonly SystemSpecsCollector _collector;
    private readonly ILogger<SystemSpecsPrewarmService> _logger;

    public SystemSpecsPrewarmService(SystemSpecsCollector collector, ILogger<SystemSpecsPrewarmService> logger)
    {
        _collector = collector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield once so StartAsync returns and the rest of host start can
        // finish. Without this, the synchronous Get() below would run on the
        // startup thread before /ping is reachable.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        // LHM's Computer.Open runs on its own background task (~1-3 s typical,
        // up to ~10 s on chipsets with many SuperIO chips). Before it finishes,
        // GetCpuModel/Motherboard/GpuModels return "" and Get() refuses to
        // cache that. Retry with backoff until LHM lands or the deadline hits.
        // Deadline is 30 s — covers the slowest open we've measured with
        // headroom; longer than that and something is genuinely wrong, no
        // amount of polling will help.
        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(30);
        var attempt = 0;
        Nexus.Service.Models.Sensors.SystemSpecsResponse? lastBuild = null;
        while (!stoppingToken.IsCancellationRequested && sw.Elapsed < deadline)
        {
            attempt++;
            try
            {
                lastBuild = _collector.Get(force: true);
                if (SystemSpecsCollector.IsCacheable(lastBuild))
                {
                    _logger.LogInformation(
                        "SystemSpecs prewarm completed in {Ms} ms (attempt {Attempt}).",
                        sw.ElapsedMilliseconds, attempt);
                    return;
                }
                _logger.LogDebug(
                    "SystemSpecs prewarm attempt {Attempt} returned partial LHM fields ({Ms} ms); retrying.",
                    attempt, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemSpecs prewarm attempt {Attempt} threw; retrying.", attempt);
            }
            try { await Task.Delay(500, stoppingToken); }
            catch { return; }
        }
        // Deadline hit without LHM populating every field. Commit whatever we
        // got so headless / no-GPU configurations don't re-run the PowerShell
        // enrichment on every /system/specs request.
        if (lastBuild is not null)
        {
            _collector.CommitPartial(lastBuild);
            _logger.LogWarning(
                "SystemSpecs prewarm deadline hit after {Ms} ms ({Attempt} attempts); committing partial result " +
                "(processor={HasCpu}, motherboard={HasMobo}, graphicsCard={HasGpu}).",
                sw.ElapsedMilliseconds, attempt,
                !string.IsNullOrWhiteSpace(lastBuild.Processor),
                !string.IsNullOrWhiteSpace(lastBuild.Motherboard),
                !string.IsNullOrWhiteSpace(lastBuild.GraphicsCard));
        }
    }
}
