using System;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Minimal HID device abstraction. Covers feature-report send/receive, which is
/// all most gaming peripheral config protocols (Razer, Logitech HID++, etc.) need.
/// </summary>
public interface IHidDevice : IDisposable
{
    int VendorId { get; }
    int ProductId { get; }
    string Path { get; }
    string? Serial { get; }
    int UsagePage { get; }
    int Usage { get; }

    /// <summary>Sends a feature report (typically used for vendor protocols). Byte 0 is report ID.</summary>
    bool SetFeature(ReadOnlySpan<byte> report);

    /// <summary>Reads a feature report into the provided buffer. Byte 0 is report ID on input.</summary>
    bool GetFeature(Span<byte> buffer);

    /// <summary>
    /// Reads an input report via the control path (IOCTL_HID_GET_INPUT_REPORT on Windows).
    /// Byte 0 is report ID on input. Use after a feature-report primer that triggers the device
    /// to latch telemetry into the input report; differs from <see cref="Read"/> which is interrupt-IN.
    /// </summary>
    bool GetInputReport(Span<byte> buffer);

    /// <summary>Writes an output report (interrupt OUT). Byte 0 is report ID.</summary>
    bool Write(ReadOnlySpan<byte> report);

    /// <summary>
    /// Sends an output report via the control path (HidD_SetOutputReport /
    /// SET_REPORT(Output)), not the interrupt OUT pipe. Byte 0 is report ID.
    /// Some vendor protocols (Lian Li RGB) only accept their color stream this
    /// way; a plain interrupt-OUT <see cref="Write"/> corrupts the payload.
    /// </summary>
    bool SetOutputReport(ReadOnlySpan<byte> report);

    /// <summary>
    /// Reads an input report (interrupt IN), waiting up to <paramref name="timeoutMs"/>.
    /// Returns the bytes read (&gt;0), 0 on idle/timeout, or a negative value when the
    /// device is gone or the read failed - the caller must close and reopen rather
    /// than retrying in a tight loop (a non-blocking failure otherwise pegs a core).
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);
}
