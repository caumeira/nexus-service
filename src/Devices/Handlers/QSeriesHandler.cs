using System;
using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>
/// HYTE Q-series AIO LCD displays (Q60 and Q80). The two variants share
/// hardware behavior and the same on-device runtime; only the USB PIDs
/// differ across the device's USB modes, so they map to the same handler.
///
/// Detection is name-first because the panel's USB stack rides MediaTek's
/// SoC bridge: VIDs and PIDs change between modes (bootloader, runtime,
/// ADB interface) and across firmware revisions. The BusReportedDeviceDesc
/// the host pulls from the descriptor is stable across all modes —
/// bench-verified strings on a real Q60 (2026-05-18):
///   - <c>"HYTE Q60 Display"</c> (composite + ADB interface entries)
///   - <c>"HYTE THICC Q60"</c> (alternate descriptor in some modes)
/// Q80 firmware reports the analogous <c>"HYTE Q80 Display"</c> /
/// <c>"HYTE THICC Q80"</c> strings.
///
/// The Identifiers list is kept for the legacy bench-observed VID/PID
/// pairs but no longer authoritative — <see cref="IsConnected"/> will
/// also report true on any USB device whose name matches a Q-series
/// product string, regardless of the (VID, PID) it currently enumerates
/// under.
/// </summary>
public sealed class QSeriesHandler : IDeviceHandler
{
    private const int MediatekVid = 0x0E8D;
    private const int HyteVid = 0x3402;

    /// <summary>
    /// Product-name fragments that, when found in the USB descriptor's
    /// product string, identify a Q-series device. Match is case-
    /// insensitive substring against <see cref="UsbDeviceEntry.Name"/>.
    /// </summary>
    private static readonly string[] NameMarkers = new[] { "Q60", "Q80" };

    /// <summary>
    /// Vendor IDs that have been seen claiming Q-series products in the
    /// wild. Used to disambiguate generic substrings like "Q60" — we
    /// only trust the name match when the VID is one of these.
    /// </summary>
    private static readonly HashSet<int> KnownQseriesVids = new() { MediatekVid, HyteVid };

    public string Id => "qseries";
    public string Name => "Q-series";
    public string Category => "display";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        // Bench-observed (VID, PID) pairs. Not used by IsConnected
        // anymore (we name-match instead), kept for documentation +
        // PnP catalog joins that may want them later.
        new UsbId(MediatekVid, 0x201D), // Q60 composite, runtime mode
        new UsbId(MediatekVid, 0x201C), // Q60 alternate / Q80 sibling
        new UsbId(MediatekVid, 0x2048), // Q60 ADB-interface mode
        new UsbId(HyteVid, 0x0400),     // "HYTE THICC Q60" descriptor
        new UsbId(HyteVid, 0x0600),     // legacy/expected
        new UsbId(HyteVid, 0x0603),     // legacy/expected
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(MatchesQseries);

    private static bool MatchesQseries(UsbDeviceEntry d)
    {
        if (!KnownQseriesVids.Contains(d.VendorId)) return false;
        if (string.IsNullOrEmpty(d.Name)) return false;
        foreach (var marker in NameMarkers)
        {
            if (d.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public string GetFirmwareVersion() => "";
}
