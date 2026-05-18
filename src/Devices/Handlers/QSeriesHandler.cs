using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>
/// HYTE Q-series AIO LCD displays (Q60 and Q80). The two variants share
/// hardware behavior and the same on-device runtime; only the USB PID
/// differs, so they map to the same handler.
///
/// USB-IF enumeration uses MediaTek's VID (0x0E8D) — not HYTE's own
/// 0x3402 — because the panel's USB stack rides MediaTek's SoC bridge.
/// Bench-verified on a Q60 (2026-05-18): the device shows up under
/// <c>USB\VID_0E8D&amp;PID_201D\&lt;adb-serial&gt;</c>. The HYTE-VID
/// entries are kept as defensive aliases in case a future firmware
/// variant ships with HYTE's own VID claimed.
/// </summary>
public sealed class QSeriesHandler : IDeviceHandler
{
    private const int MediatekVid = 0x0E8D;
    private const int HyteVid = 0x3402;

    public string Id => "qseries";
    public string Name => "Q-series";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        // Bench-observed runtime-mode PIDs under MediaTek's VID.
        new UsbId(MediatekVid, 0x201D), // Q60 (verified)
        new UsbId(MediatekVid, 0x201C), // Q80 (probable — derived from sibling PID seen on Y70)
        // Defensive fallback: original HYTE-VID assignments. Not seen on
        // current firmware but listed so a future revision that adopts
        // them still maps to this handler.
        new UsbId(HyteVid, 0x0600), // Q60 (legacy/expected)
        new UsbId(HyteVid, 0x0603), // Q80 (legacy/expected)
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
