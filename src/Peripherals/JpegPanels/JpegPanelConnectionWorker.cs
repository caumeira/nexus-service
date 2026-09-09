using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Holds one <see cref="JpegPanelHub"/> attached while its panel is present and Nexus
/// Control is on for it. There is nothing to poll on these devices - no telemetry, no
/// status reports - so this only watches for arrival and departure.
///
/// Departure is detected by the frame path failing, not by enumeration: a panel that
/// stops accepting writes is gone whether or not Windows has retired its HID node yet.
/// </summary>
public sealed class JpegPanelConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PresencePollMs = 2000;

    private readonly IHidEnumerator _hid;
    private readonly JpegPanelHub _hub;
    private readonly DeviceControlGate _gate;

    // A panel that enumerates but exposes no report long enough to carry a frame chunk is
    // indistinguishable from an absent one in the loop below, so say which it was - once
    // per run of misses, since the loop retries every few seconds.
    private bool _missLogged;

    public JpegPanelConnectionWorker(IHidEnumerator hid, JpegPanelHub hub, DeviceControlGate gate)
    {
        _hid = hid;
        _hub = hub;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var model = _hub.Model;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(model.HandlerId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device == null)
                {
                    LogMissOnce();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                _missLogged = false;

                if (!_hub.Attach(device))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceLog.Info($"[{model.HandlerId}] connected: {model.Name}, {model.Width}x{model.Height}");
                try
                {
                    while (!stoppingToken.IsCancellationRequested
                        && _gate.IsEnabled(model.HandlerId)
                        && _hub.IsConnected
                        && StillPresent())
                    {
                        await Task.Delay(PresencePollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _hub.Detach();
                    ServiceLog.Info($"[{model.HandlerId}] disconnected");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{_hub.Model.HandlerId}] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        _hub.Detach();
    }

    private void LogMissOnce()
    {
        if (_missLogged)
        {
            return;
        }
        _missLogged = true;
        var model = _hub.Model;
        var seen = new System.Text.StringBuilder();
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            foreach (var info in _hid.Find(model.VendorId, model.ProductIds[i]))
            {
                seen.Append(seen.Length == 0 ? "" : "; ")
                    .Append($"pid=0x{info.ProductId:X4} usage={info.UsagePage:X4}/{info.Usage:X2} out={info.OutputReportByteLength} in={info.InputReportByteLength}");
            }
        }
        ServiceLog.Info(seen.Length == 0
            ? $"[{model.HandlerId}] no HID interface enumerated for this panel"
            : $"[{model.HandlerId}] no interface carries a {model.ReportLength}-byte report; saw {seen}");
    }

    private bool StillPresent()
    {
        var model = _hub.Model;
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            if (_hid.Find(model.VendorId, model.ProductIds[i]).Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    private IHidDevice? FindAndOpen()
    {
        var model = _hub.Model;
        HidDeviceInfo? best = null;
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            foreach (var info in _hid.Find(model.VendorId, model.ProductIds[i]))
            {
                // These coolers expose several HID collections; the panel is the one whose
                // output reports are long enough to carry a frame chunk. Matching on the
                // declared length rather than a usage page keeps this working across the
                // family, whose collections are not consistently tagged.
                if (info.OutputReportByteLength < model.ReportLength)
                {
                    continue;
                }
                if (best == null || info.OutputReportByteLength < best.OutputReportByteLength)
                {
                    best = info;
                }
            }
        }
        // forInput: a model with a control channel reads the panel's replies, and a
        // non-overlapped handle would block that read with no timeout to bound it.
        return best == null ? null : _hid.Open(best.Path, forInput: true);
    }
}
