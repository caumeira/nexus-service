using System;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// The Kraken's bulk OUT pipe, which carries LCD pixel data. Separate from the HID
/// control channel: the cooler is a composite device whose interface 0 is bound to
/// WinUSB and whose interface 1 is HID.
/// </summary>
public interface IKrakenLcdTransport : IDisposable
{
    /// <summary>Writes one bulk transfer. The caller decides the framing.</summary>
    bool Write(ReadOnlySpan<byte> data);
}

/// <summary>Opens the bulk pipe for a cooler with the given USB serial, or null when unavailable.</summary>
public interface IKrakenLcdTransportFactory
{
    IKrakenLcdTransport? Open(string? serial);
}
