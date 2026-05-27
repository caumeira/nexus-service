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

        try
        {
            var sw = Stopwatch.StartNew();
            _collector.Get(force: true);
            _logger.LogInformation("SystemSpecs prewarm completed in {Ms} ms.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Never fatal — the next /system/specs request will rebuild on
            // demand. Log so we notice if the prewarm is silently failing.
            _logger.LogWarning(ex, "SystemSpecs prewarm failed; first request will pay cold start.");
        }
    }
}
