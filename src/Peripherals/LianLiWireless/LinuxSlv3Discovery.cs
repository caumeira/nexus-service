using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// sysfs discovery for the SLV3 TX/RX dongles: each matching USB device maps to
/// its usbfs node <c>/dev/bus/usb/BBB/DDD</c>, which <see cref="LinuxSlv3Transport"/> opens.
/// The dongles report no serial, so the sysfs port path stands in for one.
/// </summary>
public sealed class LinuxSlv3Discovery : ISlv3Discovery
{
    private const string SysfsRoot = "/sys/bus/usb/devices";

    public IReadOnlyList<Slv3PortInfo> Discover() => DiscoverFrom(SysfsRoot);

    /// <summary>Discover from an arbitrary sysfs root. Exposed for unit tests.</summary>
    internal static IReadOnlyList<Slv3PortInfo> DiscoverFrom(string root)
    {
        var result = new List<Slv3PortInfo>();
        if (!Directory.Exists(root))
        {
            return result;
        }
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // "N-M.P" is a device; "usbN" root hubs and "N-M:C.I" interfaces have no dongle identity.
            var name = Path.GetFileName(dir);
            if (name.StartsWith("usb", StringComparison.Ordinal) || name.Contains(':'))
            {
                continue;
            }
            if (!TryReadHex(Path.Combine(dir, "idVendor"), out var vid)
                || !TryReadHex(Path.Combine(dir, "idProduct"), out var pid)
                || !TryResolveRole(vid, pid, out var role)
                || !TryReadInt(Path.Combine(dir, "busnum"), out var bus)
                || !TryReadInt(Path.Combine(dir, "devnum"), out var dev))
            {
                continue;
            }
            var serial = TryRead(Path.Combine(dir, "serial"));
            result.Add(new Slv3PortInfo
            {
                PortName = $"/dev/bus/usb/{bus:D3}/{dev:D3}",
                Serial = string.IsNullOrEmpty(serial) ? name : serial,
                Role = role,
            });
        }
        return result;
    }

    private static bool TryResolveRole(int vid, int pid, out Slv3DongleRole role)
    {
        if ((vid == Slv3Protocol.TxVendorId && pid == Slv3Protocol.TxProductId)
            || (vid == Slv3Protocol.WchVendorId && pid == Slv3Protocol.TxProductIdWch))
        {
            role = Slv3DongleRole.Tx;
            return true;
        }
        if ((vid == Slv3Protocol.RxVendorId && pid == Slv3Protocol.RxProductId)
            || (vid == Slv3Protocol.WchVendorId && pid == Slv3Protocol.RxProductIdWch))
        {
            role = Slv3DongleRole.Rx;
            return true;
        }
        role = default;
        return false;
    }

    private static bool TryReadHex(string path, out int value) =>
        int.TryParse(TryRead(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool TryReadInt(string path, out int value) =>
        int.TryParse(TryRead(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
