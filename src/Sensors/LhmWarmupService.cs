using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Cooling;

namespace Nexus.Service.Sensors;

/// <summary>
/// Forces construction of the <see cref="IFanControlProvider"/> DI singleton
/// on a thread-pool thread after host start. Resolving it transitively triggers
/// the <see cref="LhmComputer"/> ctor (whose <see cref="LibreHardwareMonitor.Hardware.Computer.Open"/>
/// is itself a background <c>Task.Run</c>); the resolve must happen before the
/// first HTTP request touches <c>IFanControlProvider</c>, else that request
/// pays for the graph construction inline.
///
/// Injecting <c>IFanControlProvider</c> directly would resolve it at this
/// service's construction time, during host startup. Taking an
/// <see cref="IServiceProvider"/> and resolving inside <see cref="ExecuteAsync"/>
/// after <see cref="Task.Yield"/> keeps the construction off the critical path.
///
/// Warmups belong in a post-StartAsync BackgroundService, not on the startup
/// critical path.
/// </summary>
public sealed class LhmWarmupService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<LhmWarmupService> _logger;

    public LhmWarmupService(IServiceProvider sp, ILogger<LhmWarmupService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield once so StartAsync returns and the rest of host start
        // (Kestrel bind + ApplicationStarted) completes immediately.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        try
        {
            var sw = Stopwatch.StartNew();
            _sp.GetRequiredService<IFanControlProvider>();
            _logger.LogInformation("LHM warmup (IFanControlProvider resolve) completed in {Ms} ms.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Never fatal - the next /cooling or /sensors request will pay
            // the singleton ctor inline. Logged so silent regressions are
            // discoverable.
            _logger.LogWarning(ex, "LHM warmup failed; first cooling/sensors request will pay cold start.");
        }
    }
}
