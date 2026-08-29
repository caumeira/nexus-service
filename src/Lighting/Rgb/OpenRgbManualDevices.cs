using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Devices the bundled daemon can only find through a registration, because the
/// hardware advertises nothing a detector can match: a QMK-OpenRGB board answers
/// only on the vid/pid it was flashed with (QMKOpenRGBControllerDetect.cpp
/// registers one dynamic detector per configured entry), and an E1.31 / WLED
/// device is a bare IP with no enumeration at all.
///
/// Both lists live in the daemon's OpenRGB.json, which it reads once at launch,
/// so a change here needs the subprocess bounced the same way a detector
/// override does.
/// </summary>
public static class OpenRgbManualDeviceConfig
{
    /// <summary>Settings object the QMK-OpenRGB dynamic detector reads.</summary>
    private const string QmkSection = "QMKOpenRGBDevices";
    /// <summary>Settings object the E1.31 detector reads.</summary>
    private const string E131Section = "E131Devices";

    /// <summary>
    /// Merges the configured registrations into the daemon's config, replacing
    /// each section wholesale (they are service-owned once the user has any
    /// entry). Returns true when the file changed.
    /// </summary>
    public static bool Write(string configDir, OpenRgbManualDevices devices)
    {
        try
        {
            var path = Path.Combine(configDir, "OpenRGB.json");
            JsonObject root;
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var desiredQmk = BuildQmk(devices.Qmk);
            var desiredE131 = BuildE131(devices.E131);
            var changed = false;

            if (!NodeEquals(root[QmkSection], desiredQmk))
            {
                root[QmkSection] = desiredQmk;
                changed = true;
            }
            if (!NodeEquals(root[E131Section], desiredE131))
            {
                root[E131Section] = desiredE131;
                changed = true;
            }

            if (changed)
            {
                File.WriteAllText(path, root.ToJsonString());
                ServiceLog.Info($"[openrgb-proc] manual device registrations written ({devices.Qmk.Count} QMK, {devices.E131.Count} E1.31) in {path}");
            }
            return changed;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[openrgb-proc] manual-device write failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static JsonObject BuildQmk(IReadOnlyList<QmkOpenRgbDeviceEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var vid = NormalizeHex(e.UsbVid);
            var pid = NormalizeHex(e.UsbPid);
            if (!IsValidHexId(vid) || !IsValidHexId(pid))
            {
                ServiceLog.Warn($"[openrgb-proc] skipping QMK registration '{e.Name}': usb_vid/usb_pid must be 16-bit hex, got '{e.UsbVid}'/'{e.UsbPid}'");
                continue;
            }
            arr.Add((JsonNode)new JsonObject
            {
                ["name"] = e.Name ?? "",
                ["usb_vid"] = vid,
                ["usb_pid"] = pid,
            });
        }
        return new JsonObject { ["devices"] = arr };
    }

    private static JsonObject BuildE131(IReadOnlyList<E131DeviceEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var s = Sanitize(e);
            if (s.Ip.Length == 0) continue;
            arr.Add((JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["ip"] = s.Ip,
                ["num_leds"] = s.NumLeds,
                ["start_universe"] = s.StartUniverse,
                ["start_channel"] = s.StartChannel,
                ["keepalive_time"] = s.KeepaliveTime,
                ["universe_size"] = s.UniverseSize,
            });
        }
        return new JsonObject { ["devices"] = arr };
    }

    /// <summary>Strips an 0x prefix and upper-cases; the daemon wants bare hex.</summary>
    public static string NormalizeHex(string? value)
    {
        var s = (value ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.ToUpperInvariant();
    }

    /// <summary>
    /// A 16-bit hex id the daemon can parse. RegisterQMKDetectors calls
    /// std::stoi(s, 0, 16) inside ProcessDynamicDetectors, which has no
    /// try/catch, so a malformed id terminates the daemon on every launch and
    /// crash-loops it. Nothing malformed may reach the file.
    /// </summary>
    public static bool IsValidHexId(string? value)
    {
        var s = value ?? "";
        if (s.Length is 0 or > 4) return false;
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>Clamps the E1.31 numerics: the controller stores them unsigned and divides by universe_size, so a negative or zero value wraps or spins.</summary>
    public static E131DeviceEntry Sanitize(E131DeviceEntry e) => new()
    {
        Name = e.Name ?? "",
        Ip = (e.Ip ?? "").Trim(),
        NumLeds = Math.Clamp(e.NumLeds, 0, 65_536),
        StartUniverse = Math.Clamp(e.StartUniverse, 1, 63_999),
        StartChannel = Math.Clamp(e.StartChannel, 1, 512),
        KeepaliveTime = Math.Clamp(e.KeepaliveTime, 0, 3600),
        UniverseSize = Math.Clamp(e.UniverseSize, 1, 512),
    };

    private static bool NodeEquals(JsonNode? a, JsonNode? b)
        => Canonical(a) == Canonical(b);

    /// <summary>Key-order-independent form; the daemon rewrites this file with nlohmann, which orders object keys alphabetically.</summary>
    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject o:
            {
                var keys = new List<string>(o.Count);
                foreach (var kv in o) keys.Add(kv.Key);
                keys.Sort(StringComparer.Ordinal);
                var sb = new System.Text.StringBuilder("{");
                foreach (var k in keys) sb.Append(k).Append(':').Append(Canonical(o[k])).Append(',');
                return sb.Append('}').ToString();
            }
            case JsonArray a:
            {
                var sb = new System.Text.StringBuilder("[");
                foreach (var item in a) sb.Append(Canonical(item)).Append(',');
                return sb.Append(']').ToString();
            }
            default:
                return node.ToJsonString();
        }
    }
}
