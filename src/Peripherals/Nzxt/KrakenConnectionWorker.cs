using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

public sealed class KrakenConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int StatusPollMs = 1000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly KrakenHub _hub;
    private readonly KrakenLightingDeviceProvider _lighting;
    private readonly KrakenCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;

    public KrakenConnectionWorker(
        IHidEnumerator hid,
        KrakenHub hub,
        KrakenLightingDeviceProvider lighting,
        KrakenCoolingProvider cooling,
        DeviceControlGate gate)
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
                if (!_gate.IsEnabled(KrakenHub.DeviceId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device == null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                bool started = false;
                try
                {
                    if (_hub.Connect())
                    {
                        started = true;
                        var snap = _hub.Snapshot;
                        ServiceLog.Info(
                            $"[nzxt-kraken] connected: firmware {snap.FirmwareVersion}, " +
                            $"{snap.Channels.Count} RGB channel(s), LCD bulk {(_hub.HasLcd ? "open" : "unavailable")}");
                        _lighting.OnHubStateUpdated();

                        int failures = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(KrakenHub.DeviceId))
                        {
                            if (_hub.Poll())
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
                            _lighting.OnHubStateUpdated();
                            await Task.Delay(StatusPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Runs whenever Attach ran, including when Connect throws.
                    _hub.Detach();
                    if (started)
                    {
                        ServiceLog.Info("[nzxt-kraken] disconnected");
                    }
                    _lighting.OnHubStateUpdated();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[nzxt-kraken] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(KrakenProtocol.VendorId, KrakenProtocol.ProductIdKrakenEliteV2))
        {
            // The cooler exposes a single vendor-defined HID interface; anything whose
            // reports are too short to carry a command is the wrong one.
            if (info.OutputReportByteLength < KrakenProtocol.ReportLength
                || info.InputReportByteLength < KrakenProtocol.ReportLength)
            {
                continue;
            }
            if (best == null || info.InputReportByteLength > best.InputReportByteLength)
            {
                best = info;
            }
        }
        if (best == null)
        {
            return null;
        }
        // forInput so Read honors its timeout instead of busy-spinning on Windows.
        return _hid.Open(best.Path, forInput: true);
    }
}
