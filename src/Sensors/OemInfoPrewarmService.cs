using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Sensors;

/// <summary>
/// Touches <see cref="OemInfo.Manufacturer"/> in the background after the
/// host reports ready, so the Lazy backing it resolves before the first
/// caller (app-listing routes) needs the value.
///
/// Must not delay host startup. <see cref="Task.Yield"/> hands control back to
/// <c>BackgroundService.StartAsync</c> immediately so the host completes boot
/// and <c>/ping</c> responds while the read runs on a thread-pool thread.
/// </summary>
public sealed class OemInfoPrewarmService : BackgroundService
{
    private readonly OemInfo _oemInfo;

    public OemInfoPrewarmService(OemInfo oemInfo)
    {
        _oemInfo = oemInfo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        _ = _oemInfo.Manufacturer;
    }
}
