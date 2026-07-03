using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Connects the SLV3 dongles and drives the hub's periodic tick (device-list
/// refresh, keepalive re-assert, bind/unbind state machine). The tick cadence
/// matches the firmware's spec'd keepalive interval
/// (plans/lianli-wireless-support.md section 3), not a race-avoidance sleep.
/// </summary>
public sealed class Slv3ConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int TickPollMs = 1000;
    private const int MaxConsecutiveFailures = 3;

    private readonly Slv3Hub _hub;
    private readonly Slv3LightingDeviceProvider _lighting;
    private readonly IConfigStore _store;
    private readonly DeviceControlGate _gate;

    public Slv3ConnectionWorker(Slv3Hub hub, Slv3LightingDeviceProvider lighting, IConfigStore store, DeviceControlGate gate)
    {
        _hub = hub;
        _lighting = lighting;
        _store = store;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("lianli-wireless"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // WinUSB is exclusive-open: L-Connect must release the dongles before
                // EnsureConnected opens them, so stop it once they are present but before
                // the open (unlike the wired HID hub, which can open alongside L-Connect).
                if (OperatingSystem.IsWindows()
                    && _store.Load().Devices.LianLiWireless.StopConflictingApps
                    && _hub.DonglesPresent())
                {
                    // Watcher stopped first so it cannot restart the main service.
                    StopLConnectServices();
                }

                if (_hub.EnsureConnected())
                {
                    ServiceLog.Info("[lianli-wireless] connected");
                    _lighting.OnHubStateUpdated();
                    try
                    {
                        var failures = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("lianli-wireless"))
                        {
                            if (_hub.DriveTick())
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
                            // Picks up newly bound/unbound fan chains without
                            // waiting for the RgbBridge periodic poll.
                            _lighting.OnHubStateUpdated();
                            await Task.Delay(TickPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _hub.Disconnect();
                        ServiceLog.Info("[lianli-wireless] disconnected");
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
                ServiceLog.Error($"[lianli-wireless] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StopLConnectServices()
    {
        var watcherResult = WindowsServiceController.StopService("LConnectServiceWatcher");
        var mainResult = WindowsServiceController.StopService("LConnectService");
        if (watcherResult == ServiceStopResult.NotFound && mainResult == ServiceStopResult.NotFound)
        {
            return;
        }
        if (watcherResult == ServiceStopResult.Failed || mainResult == ServiceStopResult.Failed)
        {
            ServiceLog.Warn($"[lianli-wireless] L-Connect stop: watcher={watcherResult} main={mainResult}");
        }
        else
        {
            ServiceLog.Info("[lianli-wireless] stopped LConnectServiceWatcher and LConnectService");
        }
    }
}
