using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Qos.Service.Devices.Detection;

/// <summary>
/// macOS USB enumeration via system_profiler SPUSBDataType -json.
/// Parses the JSON output to extract vendor_id, product_id, manufacturer, serial,
/// location_id, speed, and device name for each USB device.
/// </summary>
public sealed class MacUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Enumerate()
    {
        try
        {
            var json = ShellOut("/usr/sbin/system_profiler", "SPUSBDataType", "-json");
            return ParseJson(json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usb-enum] macOS enumeration failed: {ex.Message}");
            return new List<UsbDeviceEntry>();
        }
    }

    /// <summary>Pure parser exposed for unit tests.</summary>
    internal static List<UsbDeviceEntry> ParseJson(string json)
    {
        var result = new List<UsbDeviceEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("SPUSBDataType", out var root))
        {
            foreach (var controller in root.EnumerateArray())
            {
                CollectDevices(controller, result);
            }
        }
        return result;
    }

    private static void CollectDevices(JsonElement element, List<UsbDeviceEntry> result)
    {
        if (element.TryGetProperty("vendor_id", out var vidEl) &&
            element.TryGetProperty("product_id", out var pidEl))
        {
            var vid = ParseHexId(vidEl.GetString());
            var pid = ParseHexId(pidEl.GetString());
            if (vid > 0 && pid > 0)
            {
                var name = TryGetString(element, "_name");
                var manufacturer = ExtractManufacturer(vidEl.GetString(), element);
                var serial = TryGetString(element, "serial_num");
                var location = TryGetString(element, "location_id");
                var speed = TryGetString(element, "speed");

                result.Add(new UsbDeviceEntry
                {
                    VendorId = vid,
                    ProductId = pid,
                    Name = name,
                    Manufacturer = manufacturer,
                    Serial = serial,
                    Location = location,
                    Class = "",
                    Speed = speed,
                    Driver = "",
                    HardwareId = $"USB\\VID_{vid:X4}&PID_{pid:X4}",
                });
            }
        }

        if (element.TryGetProperty("_items", out var items))
        {
            foreach (var child in items.EnumerateArray())
            {
                CollectDevices(child, result);
            }
        }
    }

    /// <summary>
    /// system_profiler puts manufacturer inline in vendor_id: "0x3402 (HYTE)".
    /// Fall back to a "manufacturer" key when present (some entries have both).
    /// </summary>
    private static string ExtractManufacturer(string? vendorRaw, JsonElement element)
    {
        var mfg = TryGetString(element, "manufacturer");
        if (!string.IsNullOrEmpty(mfg))
        {
            return mfg;
        }
        if (string.IsNullOrEmpty(vendorRaw))
        {
            return "";
        }
        var openParen = vendorRaw.IndexOf('(');
        var closeParen = vendorRaw.IndexOf(')');
        if (openParen > 0 && closeParen > openParen)
        {
            return vendorRaw.Substring(openParen + 1, closeParen - openParen - 1).Trim();
        }
        return "";
    }

    private static string TryGetString(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
    }

    private static int ParseHexId(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }
        var hex = raw.Trim();
        var spaceIdx = hex.IndexOf(' ');
        if (spaceIdx > 0)
        {
            hex = hex.Substring(0, spaceIdx);
        }
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var val) ? val : 0;
    }

    private static string ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "";
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
