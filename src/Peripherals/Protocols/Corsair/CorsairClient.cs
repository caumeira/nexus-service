using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Protocols.Corsair;

/// <summary>
/// Corsair mouse/keyboard vendor-protocol transport. Currently unused — no
/// model in <see cref="CorsairPeripheralFactory.Models"/> sets
/// <c>HasProtocol = true</c>, so this client is never constructed. Retained
/// for future "Bragi"-generation mice that use HID feature reports.
///
/// Uses 65-byte HID feature reports (report ID prefix + 64 byte payload) on
/// the control interface (usually MI_01). Protocol structure derived from
/// ckb-next / OpenRGB observations of the pre-iCUE-4 generation of Corsair
/// peripherals.
///
/// Command frame (the 64-byte payload, after the report ID prefix):
/// <code>
///   byte 0 : command  (0x01 = write property, 0x02 = read property, etc.)
///   byte 1 : property id
///   byte 2..63 : args
/// </code>
///
/// Reads write the request first, then read back the reply as a second
/// feature-report exchange. Thread-safe via a lock so multiple capability
/// calls serialize.
/// </summary>
public sealed class CorsairClient : IDisposable
{
    public const int ReportSize = 65;     // 1 byte report id + 64 byte payload
    public const byte ReportId = 0x00;    // Corsair devices accept report id 0 for feature reports
    public const byte CmdWrite = 0x01;
    public const byte CmdRead = 0x02;

    private readonly IHidDevice _device;
    private readonly object _lock = new();

    public CorsairClient(IHidDevice device)
    {
        _device = device;
    }

    /// <summary>Sends a command with no expected reply. Returns success.</summary>
    public bool Write(byte property, ReadOnlySpan<byte> args)
    {
        lock (_lock)
        {
            var buf = new byte[ReportSize];
            buf[0] = ReportId;
            buf[1] = CmdWrite;
            buf[2] = property;
            args.CopyTo(buf.AsSpan(3));
            return _device.SetFeature(buf);
        }
    }

    /// <summary>
    /// Sends a read request and returns the reply payload (bytes 1..64 of the reply).
    /// Returns null on failure.
    /// </summary>
    public byte[]? Read(byte property, int replyDelayMs = 15)
    {
        lock (_lock)
        {
            var req = new byte[ReportSize];
            req[0] = ReportId;
            req[1] = CmdRead;
            req[2] = property;
            if (!_device.SetFeature(req))
            {
                return null;
            }

            Thread.Sleep(replyDelayMs);

            var reply = new byte[ReportSize];
            reply[0] = ReportId;
            if (!_device.GetFeature(reply))
            {
                return null;
            }

            var payload = new byte[64];
            Array.Copy(reply, 1, payload, 0, 64);
            return payload;
        }
    }

    public void Dispose() => _device.Dispose();
}
