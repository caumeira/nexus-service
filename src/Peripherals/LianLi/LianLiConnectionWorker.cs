using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLi;

public sealed class LianLiConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int RpmPollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly LianLiHub _hub;
    private readonly LianLiLightingDeviceProvider _lighting;

    public LianLiConnectionWorker(IHidEnumerator hid, LianLiHub hub, LianLiLightingDeviceProvider lighting)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var device = FindAndOpen();
                if (device != null)
                {
                    _hub.Attach(device);
                    ServiceLog.Info("[lianli] connected");
                    _lighting.OnHubStateUpdated();
                    try
                    {
                        var failures = 0;
                        while (!stoppingToken.IsCancellationRequested)
                        {
                            if (_hub.ReadRpm())
                            {
                                failures = 0;
                            }
                            else
                            {
                                failures++;
                                if (failures >= MaxConsecutiveFailures)
                                {
                                    break;
                                }
                            }
                            // Picks up fan-count edits (zone LED counts) without
                            // waiting for the RgbBridge periodic poll.
                            _lighting.OnHubStateUpdated();
                            await Task.Delay(RpmPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _hub.Detach();
                        ServiceLog.Info("[lianli] disconnected");
                        _lighting.OnHubStateUpdated();
                    }
                }
                else
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        var infos = _hid.Find(LianLiProtocol.VendorId, LianLiProtocol.ProductId);
        foreach (var info in infos)
        {
            if (info.UsagePage == LianLiProtocol.VendorUsagePage
                && info.Usage == LianLiProtocol.VendorUsage)
            {
                return _hid.Open(info.Path);
            }
        }
        return null;
    }
}
