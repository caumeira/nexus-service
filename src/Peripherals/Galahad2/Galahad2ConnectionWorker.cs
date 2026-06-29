using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Galahad2;

public sealed class Galahad2ConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int RpmPollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly Galahad2Hub _hub;
    private readonly Galahad2CoolingProvider _cooling;

    public Galahad2ConnectionWorker(IHidEnumerator hid, Galahad2Hub hub, Galahad2CoolingProvider cooling)
    {
        _hid = hid;
        _hub = hub;
        _cooling = cooling;
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
                    bool started = false;
                    try
                    {
                        if (_hub.Connect())
                        {
                            started = true;
                            // untested - verification pending
                            ServiceLog.Info("[lianli-aio] connected");
                            int failures = 0;
                            while (!stoppingToken.IsCancellationRequested)
                            {
                                if (_hub.PollRpm())
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
                                await Task.Delay(RpmPollMs, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // Detach always runs when Attach was called, even if Connect throws.
                        _hub.Detach();
                        if (started)
                        {
                            ServiceLog.Info("[lianli-aio] disconnected");
                        }
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
                ServiceLog.Error($"[lianli-aio] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        int[] pids = { Galahad2Protocol.ProductIdPerformance, Galahad2Protocol.ProductIdRegular };
        HidDeviceInfo? best = null;
        foreach (int pid in pids)
        {
            var infos = _hid.Find(Galahad2Protocol.VendorId, pid);
            foreach (var info in infos)
            {
                if (info.OutputReportByteLength < CommandPacket.Length || info.InputReportByteLength <= 0)
                {
                    continue;
                }
                if (best == null || info.InputReportByteLength > best.InputReportByteLength)
                {
                    best = info;
                }
            }
        }
        if (best == null)
        {
            return null;
        }
        // forInput=true for overlapped I/O so Read honors its timeout on Windows.
        return _hid.Open(best.Path, forInput: true);
    }
}
