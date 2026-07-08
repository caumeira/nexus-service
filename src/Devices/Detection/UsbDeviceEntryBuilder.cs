using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Builds <see cref="UsbDeviceEntry"/> rows from raw PnP device records
/// (instance id + device properties). Shared by the Windows CfgMgr32
/// enumerator and its unit tests; lives in a cross-platform file so the
/// logic is testable off-Windows.
/// </summary>
internal static class UsbDeviceEntryBuilder
{
    /// <summary>
    /// Appends an entry for a `USB\VID_xxxx&amp;PID_yyyy[&amp;MI_zz]\serial`
    /// instance. Skips non-USB instances and root hubs (not user-facing).
    /// Prefers the USB descriptor's own product string (BusReportedDeviceDesc)
    /// over the driver's generic Device Description. Composite-device
    /// interface nodes share the parent's BusReportedDeviceDesc, so the
    /// (VID, PID, Name) dedupe collapses them to a single row.
    /// </summary>
    public static void Append(
        List<UsbDeviceEntry> result, HashSet<string> seen,
        string instanceId, string busReportedDesc, string deviceDescription,
        string manufacturer, string className, string driverName, string locationInfo)
    {
        if (!instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (instanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var (vid, pid, deviceKey, serial) = ParseInstanceId(instanceId);
        if (vid == 0 || pid == 0)
        {
            return;
        }

        var name = busReportedDesc.Length > 0 ? busReportedDesc : deviceDescription;

        var dedupeKey = $"{vid:X4}:{pid:X4}:{name}";
        if (!seen.Add(dedupeKey))
        {
            return;
        }

        result.Add(new UsbDeviceEntry
        {
            VendorId = vid,
            ProductId = pid,
            Name = name,
            Manufacturer = manufacturer,
            Serial = serial,
            Location = locationInfo,
            Class = className,
            Speed = "",
            Driver = driverName,
            HardwareId = $"USB\\{deviceKey}",
        });
    }

    /// <summary>
    /// Instance ID format: `USB\VID_xxxx&amp;PID_yyyy[&amp;MI_zz]\SERIAL_OR_PARENT_ID`.
    /// </summary>
    public static (int Vid, int Pid, string DeviceKey, string Serial) ParseInstanceId(string instanceId)
    {
        var body = instanceId.Length > 4 ? instanceId.AsSpan(4) : ReadOnlySpan<char>.Empty;
        var slash = body.IndexOf('\\');
        var deviceKeySpan = slash > 0 ? body.Slice(0, slash) : body;
        var serialSpan = slash > 0 ? body.Slice(slash + 1) : ReadOnlySpan<char>.Empty;

        int vid = 0, pid = 0;
        foreach (var partRange in deviceKeySpan.Split('&'))
        {
            var part = deviceKeySpan[partRange];
            if (part.Length >= 8 && part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(part.Slice(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
            }
            else if (part.Length >= 8 && part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(part.Slice(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
            }
        }
        return (vid, pid, deviceKeySpan.ToString(), serialSpan.ToString());
    }
}
