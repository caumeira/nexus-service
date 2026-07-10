using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
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
    private readonly LianLiCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;

    public LianLiConnectionWorker(IHidEnumerator hid, LianLiHub hub, LianLiLightingDeviceProvider lighting, LianLiCoolingProvider cooling, DeviceControlGate gate)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("lianli"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen(out var profile);
                if (device != null)
                {
                    _hub.Attach(device, profile);
                    ServiceLog.Info("[lianli] connected");
                    _lighting.OnHubStateUpdated();
                    try
                    {
                        var failures = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("lianli"))
                        {
                            if (_hub.ReadRpm())
                            {
                                failures = 0;
                                _cooling.ReassertControl();
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

    private IHidDevice? FindAndOpen(out LianLiFanProfile profile)
    {
        foreach (var pid in LianLiFanProfiles.AllProductIds)
        {
            var infos = _hid.Find(LianLiProtocol.VendorId, pid);
            foreach (var info in infos)
            {
                if (info.UsagePage == LianLiProtocol.VendorUsagePage
                    && info.Usage == LianLiProtocol.VendorUsage)
                {
                    if (!LianLiFanProfiles.TryGet(pid, out profile)) continue;
                    var device = _hid.Open(info.Path);
                    if (device != null) return device;
                }
            }
        }
        profile = default;
        return null;
    }
}
