using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Computes the cross-install device fingerprint mappings are keyed by.
/// Identical hardware must produce the identical key on every install:
///
///   usb:{vid}:{pid}            HID devices - parsed from the OpenRGB
///                              Location string (the SDK protocol does not
///                              expose vid/pid yet; our fork will) or known
///                              first-party constants.
///   orgb:{hash16}              non-USB controllers (GPU i2c, motherboard
///                              SMBus) - model-granular hash of
///                              normalized vendor|name.
///   {parentKey}:zone:{index}   per-zone cards (motherboard headers).
///
/// Keys are matching identifiers, not security tokens.
/// </summary>
public static class DeviceKeyComputer
{
    public static string ForOpenRgbDevice(RgbDevice device)
    {
        if (TryParseUsbIdsFromLocation(device.Location, out var vid, out var pid))
            return $"usb:{vid}:{pid}";
        var vendor = Normalize(device.Vendor);
        var name = Normalize(device.Name);
        return $"orgb:{Hash16(vendor + "|" + name)}";
    }

    public static string ForZone(string parentKey, int zoneIndex)
        => $"{parentKey}:zone:{zoneIndex.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>First-party device key from known VID/PID plus a module discriminator (e.g. NP50 port module model).</summary>
    public static string ForFirstParty(int vid, int pid, string? module = null)
    {
        var key = $"usb:{vid:x4}:{pid:x4}";
        return string.IsNullOrEmpty(module) ? key : $"{key}:{Normalize(module)}";
    }

    /// <summary>
    /// Pull "vid_XXXX&amp;pid_XXXX" out of a Windows HID path, or the
    /// "1b1c:0c1a" shape some hidapi builds embed. Case-insensitive; returns
    /// lowercase 4-digit hex.
    /// </summary>
    internal static bool TryParseUsbIdsFromLocation(string? location, out string vid, out string pid)
    {
        vid = "";
        pid = "";
        if (string.IsNullOrEmpty(location))
        {
            return false;
        }
        var lower = location.ToLowerInvariant();
        var vidIdx = lower.IndexOf("vid_", StringComparison.Ordinal);
        var pidIdx = lower.IndexOf("pid_", StringComparison.Ordinal);
        if (vidIdx >= 0 && pidIdx >= 0
            && TryReadHex4(lower, vidIdx + 4, out vid)
            && TryReadHex4(lower, pidIdx + 4, out pid))
        {
            return true;
        }
        return false;
    }

    private static bool TryReadHex4(string s, int start, out string hex)
    {
        hex = "";
        if (start + 4 > s.Length)
            return false;
        for (int i = start; i < start + 4; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
                return false;
        }
        hex = s.Substring(start, 4);
        return true;
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var raw in value.Trim().ToLowerInvariant())
        {
            var isSpace = char.IsWhiteSpace(raw);
            if (isSpace)
            {
                if (!lastWasSpace) sb.Append(' ');
            }
            else
            {
                sb.Append(raw);
            }
            lastWasSpace = isSpace;
        }
        return sb.ToString();
    }

    private static string Hash16(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
