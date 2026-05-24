using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Linux USB enumeration via sysfs. Walks /sys/bus/usb/devices/* reading
/// idVendor, idProduct, product, manufacturer, serial, busnum, devnum, speed,
/// and bDeviceClass. Pure file reads — no subprocesses, no libusb dependency.
/// </summary>
public sealed class LinuxUsbEnumerator : IUsbEnumerator
{
    private const string SysfsRoot = "/sys/bus/usb/devices";

    public List<UsbDeviceEntry> Enumerate() => EnumerateFrom(SysfsRoot);

    /// <summary>Enumerate from an arbitrary root directory. Exposed for unit tests.</summary>
    internal static List<UsbDeviceEntry> EnumerateFrom(string root)
    {
        var result = new List<UsbDeviceEntry>();
        try
        {
            if (!Directory.Exists(root))
            {
                return result;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                // sysfs USB node names:
                //   "usbN"          → a root hub (skip — we surface the hub via its child entry below)
                //   "N-M"           → a physical device
                //   "N-M:C.I"       → an interface of a device (skip; parent already covers it)
                var name = Path.GetFileName(dir);
                if (name.StartsWith("usb", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.Contains(':'))
                {
                    continue;
                }

                var vidPath = Path.Combine(dir, "idVendor");
                var pidPath = Path.Combine(dir, "idProduct");
                if (!File.Exists(vidPath) || !File.Exists(pidPath))
                {
                    continue;
                }

                int vid = ParseHex(TryRead(vidPath));
                int pid = ParseHex(TryRead(pidPath));
                if (vid <= 0 || pid <= 0)
                {
                    continue;
                }

                var product = TryRead(Path.Combine(dir, "product"));
                var manufacturer = TryRead(Path.Combine(dir, "manufacturer"));
                var serial = TryRead(Path.Combine(dir, "serial"));
                var busnum = TryRead(Path.Combine(dir, "busnum"));
                var devnum = TryRead(Path.Combine(dir, "devnum"));
                var speedRaw = TryRead(Path.Combine(dir, "speed"));
                var classHex = TryRead(Path.Combine(dir, "bDeviceClass"));

                var deviceName = !string.IsNullOrEmpty(product)
                    ? product
                    : (!string.IsNullOrEmpty(manufacturer) ? manufacturer : "");

                var location = FormatLocation(busnum, devnum, name);
                var speed = MapSpeed(speedRaw);
                var classStr = MapClass(classHex);

                result.Add(new UsbDeviceEntry
                {
                    VendorId = vid,
                    ProductId = pid,
                    Name = deviceName,
                    Manufacturer = manufacturer,
                    Serial = serial,
                    Location = location,
                    Class = classStr,
                    Speed = speed,
                    Driver = "",
                    HardwareId = $"USB\\VID_{vid:X4}&PID_{pid:X4}",
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usb-enum] Linux enumeration failed: {ex.Message}");
        }
        return result;
    }

    private static string FormatLocation(string busnum, string devnum, string sysfsName)
    {
        if (int.TryParse(busnum, out var b) && int.TryParse(devnum, out var d))
        {
            return $"Bus {b:D3} Device {d:D3}";
        }
        return sysfsName;
    }

    /// <summary>
    /// sysfs "speed" file reports link speed in Mbit/s: 1.5, 12, 480, 5000, 10000, 20000.
    /// </summary>
    private static string MapSpeed(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }
        return raw switch
        {
            "1.5" => "Low",
            "12" => "Full",
            "480" => "High",
            "5000" => "Super",
            "10000" => "SuperPlus",
            "20000" => "SuperPlus 20",
            _ => raw + " Mbps",
        };
    }

    /// <summary>
    /// USB class code → human-readable name. Values are two-hex-digit strings per sysfs.
    /// </summary>
    private static string MapClass(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return "";
        }
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return hex;
        }
        return code switch
        {
            0x00 => "Per-Interface",
            0x01 => "Audio",
            0x02 => "Communications",
            0x03 => "HID",
            0x05 => "Physical",
            0x06 => "Image",
            0x07 => "Printer",
            0x08 => "Mass Storage",
            0x09 => "Hub",
            0x0A => "CDC Data",
            0x0B => "Smart Card",
            0x0D => "Content Security",
            0x0E => "Video",
            0x0F => "Personal Healthcare",
            0x10 => "Audio/Video",
            0x11 => "Billboard",
            0x12 => "USB-C Bridge",
            0xDC => "Diagnostic",
            0xE0 => "Wireless Controller",
            0xEF => "Miscellaneous",
            0xFE => "Application Specific",
            0xFF => "Vendor Specific",
            _ => $"0x{code:X2}",
        };
    }

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static int ParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return 0;
        }
        return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
