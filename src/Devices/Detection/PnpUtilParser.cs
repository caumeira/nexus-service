using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Pure parser for `pnputil /enum-devices /connected /properties` output.
/// `/properties` pulls the bus-reported device description
/// (<c>DEVPKEY_Device_BusReportedDeviceDesc</c>) - the USB iProduct string
/// from the device's own descriptor, e.g. "Corsair Gaming M65 Pro RGB Mouse"
/// - rather than the driver's generic Device Description (often "USB Input
/// Device").
///
/// pnputil reports only currently-attached devices.
///
/// Locale-independent: pnputil renders its field LABELS ("Instance ID:",
/// "Properties:") in the OS UI language, so matching English labels finds
/// nothing on non-English Windows (a Japanese host returned an empty list,
/// which silently disabled every USB-presence gate). The Instance ID is
/// instead identified by its VALUE (always `USB\VID_...`, never localized) and
/// the device name + location come from the canonical, untranslated
/// `DEVPKEY_*` property names. The localized Device Description / Class /
/// Manufacturer / Driver labels are still read as a best-effort fallback that
/// only fills on English hosts; the bus-reported name covers the rest.
///
/// Output is ~500 KB - 2 MB of text with 10k-25k lines. Walks the text by
/// index and materialises strings only for the values stored
/// (~O(device count)), avoiding the GC pressure of a full <c>Split('\n')</c>.
/// </summary>
internal static class PnpUtilParser
{
    private const string DescProp = "DEVPKEY_Device_BusReportedDeviceDesc";
    private const string LocationProp = "DEVPKEY_Device_LocationInfo";

    public static List<UsbDeviceEntry> Parse(string output)
    {
        var result = new List<UsbDeviceEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Top-level header fields we care about. Parsed from the indent-0
        // lines: the Instance ID by value, the rest by (English) label.
        string instanceId = "";
        string deviceDescription = "";
        string manufacturer = "";
        string className = "";
        string driverName = "";
        string busReported = "";
        string locationInfo = "";

        WantedProp currentProp = WantedProp.None;

        var span = output.AsSpan();
        foreach (var rawLineRange in span.EnumerateLines())
        {
            var line = rawLineRange; // ReadOnlySpan<char>

            if (line.IsWhiteSpace())
            {
                if (instanceId.Length != 0)
                {
                    EmitIfUsb(instanceId, deviceDescription, manufacturer, className,
                        driverName, busReported, locationInfo, result, seen);
                }
                instanceId = deviceDescription = manufacturer = className = driverName =
                    busReported = locationInfo = "";
                currentProp = WantedProp.None;
                continue;
            }

            // Top-level fields (no leading whitespace): "Label:<spaces>value".
            // The label is localized, so the Instance ID is taken from its value
            // (the only top-level value that starts `USB\`), not its label. The
            // remaining labels are matched in English as a best-effort fallback.
            if (!char.IsWhiteSpace(line[0]))
            {
                currentProp = WantedProp.None;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line.Slice(0, colon).Trim();
                var value = line.Slice(colon + 1).Trim();

                // Only allocate strings for the fields we actually use downstream.
                if (instanceId.Length == 0
                    && value.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                {
                    instanceId = value.ToString();
                }
                else if (key.Equals("Device Description", StringComparison.OrdinalIgnoreCase))
                {
                    deviceDescription = value.ToString();
                }
                else if (key.Equals("Manufacturer Name", StringComparison.OrdinalIgnoreCase))
                {
                    manufacturer = value.ToString();
                }
                else if (key.Equals("Class Name", StringComparison.OrdinalIgnoreCase))
                {
                    className = value.ToString();
                }
                else if (key.Equals("Driver Name", StringComparison.OrdinalIgnoreCase))
                {
                    driverName = value.ToString();
                }
                continue;
            }

            // Indented lines belong to the Properties block (the only indented
            // content). Keyed on canonical DEVPKEY_* names, which are not localized.
            //   4-space:  "    DEVPKEY_Device_BusReportedDeviceDesc [String]:"
            //   8-space:  "        Corsair Gaming M65 Pro RGB Mouse"
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (indent <= 4)
            {
                // Property header line. Strip the "[Type]:" suffix.
                var bracket = trimmed.IndexOf('[');
                var propName = bracket > 0
                    ? trimmed.Slice(0, bracket).Trim()
                    : trimmed.TrimEnd(':').Trim();

                if (propName.Equals(DescProp, StringComparison.OrdinalIgnoreCase))
                {
                    currentProp = WantedProp.BusReportedDesc;
                }
                else if (propName.Equals(LocationProp, StringComparison.OrdinalIgnoreCase))
                {
                    currentProp = WantedProp.LocationInfo;
                }
                else
                {
                    currentProp = WantedProp.None;
                }
                continue;
            }

            // Value line for the current property. Only capture the first value
            // for the two properties we care about.
            if (currentProp == WantedProp.BusReportedDesc && busReported.Length == 0)
            {
                busReported = trimmed.ToString();
            }
            else if (currentProp == WantedProp.LocationInfo && locationInfo.Length == 0)
            {
                locationInfo = trimmed.ToString();
            }
        }

        if (instanceId.Length != 0)
        {
            EmitIfUsb(instanceId, deviceDescription, manufacturer, className,
                driverName, busReported, locationInfo, result, seen);
        }
        return result;
    }

    private enum WantedProp { None, BusReportedDesc, LocationInfo }

    private static void EmitIfUsb(
        string instanceId, string deviceDescription, string manufacturer,
        string className, string driverName, string busReported, string locationInfo,
        List<UsbDeviceEntry> result, HashSet<string> seen)
    {
        if (!instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // Skip root hubs - not user-facing.
        if (instanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var (vid, pid, deviceKey, serial) = ExtractVidPid(instanceId);
        if (vid == 0 || pid == 0)
        {
            return;
        }

        // Prefer the USB descriptor's own product string; fall back to the driver's
        // generic Device Description. Composite-device interface nodes share the
        // parent's BusReportedDeviceDesc, so the dedupe below collapses them.
        var name = busReported.Length > 0 ? busReported : deviceDescription;

        // Dedupe by (VID, PID, Name) - collapses composite interface nodes that
        // share a parent into a single row with the brand-friendly name.
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
    private static (int Vid, int Pid, string DeviceKey, string Serial) ExtractVidPid(string instanceId)
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
