using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Protocols.Razer;

/// <summary>
/// Protocol transport for Razer HID devices. Exchanges <see cref="RazerReport"/>s over
/// HID feature reports. Thread-safe via a lock so multiple capability calls don't
/// collide — Razer devices only reliably accept one outstanding request at a time.
/// </summary>
public sealed class RazerClient : IDisposable
{
    private readonly IHidDevice _device;
    private readonly object _lock = new();

    public RazerClient(IHidDevice device)
    {
        _device = device;
    }

    /// <summary>Sends a command and optionally reads the device's response.</summary>
    public RazerReport? Exchange(RazerReport request, bool readResponse = true, int postDelayMs = 30)
    {
        lock (_lock)
        {
            var buf = request.ToHidFeatureBuffer();
            if (!_device.SetFeature(buf))
            {
                return null;
            }

            // Razer needs a brief pause between set + get feature.
            Thread.Sleep(postDelayMs);

            if (!readResponse)
            {
                return new RazerReport { Status = 0x02 };
            }

            var reply = new byte[RazerReport.HidFeatureSize];
            reply[0] = 0x00;
            if (!_device.GetFeature(reply))
            {
                return null;
            }

            var parsed = RazerReport.Parse(reply);
            return parsed;
        }
    }

    public void Dispose() => _device.Dispose();
}
