using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Conflicts;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Discovers the iCUE LINK System Hub, opens its command interface, stops Corsair
/// iCUE (which otherwise co-drives the hub and fights every write), takes the hub
/// into software mode, and polls speed/temperature telemetry while connected.
/// </summary>
public sealed class CorsairLinkConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly CorsairLinkHub _hub;
    private readonly CorsairLinkLightingDeviceProvider _lighting;
    private readonly CorsairLinkCoolingProvider _cooling;
    private readonly CorsairLinkLcd _lcd;
    private readonly IConfigStore _store;

    public CorsairLinkConnectionWorker(
        IHidEnumerator hid,
        CorsairLinkHub hub,
        CorsairLinkLightingDeviceProvider lighting,
        CorsairLinkCoolingProvider cooling,
        CorsairLinkLcd lcd,
        IConfigStore store)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _lcd = lcd;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var device = FindAndOpen();
                if (device == null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                if (OperatingSystem.IsWindows() && _store.Load().Devices.Corsair.StopConflictingApps)
                {
                    StopCorsairApps();
                }

                if (!_hub.Initialize())
                {
                    ServiceLog.Warn("[corsair] initialize failed, retrying");
                    _hub.Detach();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceLog.Info($"[corsair] connected fw={_hub.State.Firmware} devices={_hub.State.Devices.Count}");
                if (_hub.State.HasLcd)
                {
                    _lcd.DiscoverAndAttach(_hub.State.Devices, _hid);
                }
                _lighting.OnHubStateUpdated();
                _cooling.ReassertControl();
                try
                {
                    var failures = 0;
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        if (_hub.Poll())
                        {
                            failures = 0;
                            _cooling.ReassertControl();
                        }
                        else
                        {
                            failures++;
                            if (failures >= MaxConsecutiveFailures) break;
                        }
                        _lighting.OnHubStateUpdated();
                        await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _lcd.Detach();
                    _hub.Detach();
                    ServiceLog.Info("[corsair] disconnected");
                    _lighting.OnHubStateUpdated();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StopCorsairApps()
    {
        var killedAny = false;
        foreach (var def in ConflictAppCatalog.All)
        {
            if (def.Id != "icue") continue;
            foreach (var name in def.ProcessNames)
            {
                if (ProcessKiller.Kill(name)) killedAny = true;
            }
        }
        // The device-lister service re-grabs the hub if left running.
        WindowsServiceController.StopService("CorsairDeviceListerService");
        if (killedAny)
        {
            ServiceLog.Info("[corsair] stopped Corsair iCUE to take over the hub");
        }
    }

    private IHidDevice? FindAndOpen()
    {
        var infos = _hid.Find(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId);
        foreach (var info in infos)
        {
            if (info.UsagePage == CorsairLinkProtocol.VendorUsagePage
                && info.Usage == CorsairLinkProtocol.VendorUsage)
            {
                // forInput: interrupt-IN reads (the hub answers every write with a report).
                return _hid.Open(info.Path, forInput: true);
            }
        }
        return null;
    }
}
