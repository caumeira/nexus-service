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
            if (string.IsNullOrWhiteSpace(e.UsbVid) || string.IsNullOrWhiteSpace(e.UsbPid)) continue;
            // Hex strings without a prefix: the detector parses them with std::stoi(s, 0, 16).
            arr.Add((JsonNode)new JsonObject
            {
                ["name"] = e.Name,
                ["usb_vid"] = NormalizeHex(e.UsbVid),
                ["usb_pid"] = NormalizeHex(e.UsbPid),
            });
        }
        return new JsonObject { ["devices"] = arr };
    }

    private static JsonObject BuildE131(IReadOnlyList<E131DeviceEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Ip)) continue;
            arr.Add((JsonNode)new JsonObject
            {
                ["name"] = e.Name,
                ["ip"] = e.Ip,
                ["num_leds"] = e.NumLeds,
                ["start_universe"] = e.StartUniverse,
                ["start_channel"] = e.StartChannel,
                ["keepalive_time"] = e.KeepaliveTime,
                ["universe_size"] = e.UniverseSize,
            });
        }
        return new JsonObject { ["devices"] = arr };
    }

    /// <summary>Strips an 0x prefix and upper-cases; the daemon wants bare hex.</summary>
    public static string NormalizeHex(string value)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.ToUpperInvariant();
    }

    private static bool NodeEquals(JsonNode? a, JsonNode? b)
        => (a?.ToJsonString() ?? "") == (b?.ToJsonString() ?? "");
}
