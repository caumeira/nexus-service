using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Adopts the manual device registrations from a user's own OpenRGB install.
/// Anyone hitting the QMK / E1.31 gap is by definition already an OpenRGB user
/// who typed these once; importing beats making them re-enter hex ids.
///
/// Reads only the two registration sections - detector toggles and zone sizes
/// are deliberately not imported, since ours are service-owned and their zone
/// sizes are keyed to OpenRGB's controller identities, not our card ids.
/// </summary>
public static class OpenRgbConfigImport
{
    /// <summary>Ceiling for a source config; a real one is a few KB.</summary>
    public const long MaxSourceBytes = 8 * 1024 * 1024;

    /// <summary>The only filename an import will read, so a supplied path cannot point at arbitrary files.</summary>
    public const string SourceFileName = "OpenRGB.json";

    /// <summary>Where the OpenRGB GUI keeps its config per OS.</summary>
    public static string DefaultSourcePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "OpenRGB", "OpenRGB.json");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "OpenRGB", "OpenRGB.json");
        }
        return Path.Combine(home, ".config", "OpenRGB", "OpenRGB.json");
    }

    public sealed class Result
    {
        public bool Found { get; set; }
        public string Path { get; set; } = "";
        public List<QmkOpenRgbDeviceEntry> Qmk { get; set; } = new();
        public List<E131DeviceEntry> E131 { get; set; } = new();
        /// <summary>Entries the daemon could not have parsed, dropped rather than imported.</summary>
        public int Skipped { get; set; }
    }

    /// <summary>Parses the two registration sections out of an OpenRGB.json. A missing or malformed file yields an empty result rather than throwing.</summary>
    public static Result Read(string path)
    {
        var result = new Result { Path = path };
        try
        {
            if (!File.Exists(path)) return result;
            // An OpenRGB config is a few KB; the cap stops a pointed path from
            // reading something enormous into the service.
            var info = new FileInfo(path);
            if (info.Length > MaxSourceBytes)
            {
                Nexus.Service.Platform.ServiceLog.Warn($"[openrgb-import] {path} is {info.Length} bytes, over the {MaxSourceBytes} cap");
                return result;
            }
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return result;
            result.Found = true;

            if (root["QMKOpenRGBDevices"]?["devices"] is JsonArray qmk)
            {
                foreach (var node in qmk)
                {
                    if (node is not JsonObject o) continue;
                    var vid = OpenRgbManualDeviceConfig.NormalizeHex(Str(o, "usb_vid"));
                    var pid = OpenRgbManualDeviceConfig.NormalizeHex(Str(o, "usb_pid"));
                    if (!OpenRgbManualDeviceConfig.IsValidHexId(vid) || !OpenRgbManualDeviceConfig.IsValidHexId(pid))
                    {
                        result.Skipped++;
                        continue;
                    }
                    result.Qmk.Add(new QmkOpenRgbDeviceEntry
                    {
                        Name = Str(o, "name"),
                        UsbVid = vid,
                        UsbPid = pid,
                    });
                }
            }

            if (root["E131Devices"]?["devices"] is JsonArray e131)
            {
                foreach (var node in e131)
                {
                    if (node is not JsonObject o) continue;
                    var ip = Str(o, "ip");
                    if (string.IsNullOrWhiteSpace(ip)) continue;
                    result.E131.Add(OpenRgbManualDeviceConfig.Sanitize(new E131DeviceEntry
                    {
                        Name = Str(o, "name"),
                        Ip = ip,
                        NumLeds = Int(o, "num_leds", 0),
                        StartUniverse = Int(o, "start_universe", 1),
                        StartChannel = Int(o, "start_channel", 1),
                        KeepaliveTime = Int(o, "keepalive_time", 0),
                        UniverseSize = Int(o, "universe_size", 512),
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[openrgb-import] could not read {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Adds entries absent from <paramref name="into"/>, matching QMK on
    /// vid/pid and E1.31 on ip + start universe. Existing entries are left
    /// alone so a re-import never duplicates or overwrites a user's edits.
    /// Returns how many were added.
    /// </summary>
    public static int Merge(OpenRgbManualDevices into, Result imported)
    {
        var added = 0;
        foreach (var e in imported.Qmk)
        {
            var exists = false;
            foreach (var have in into.Qmk)
            {
                if (string.Equals(have.UsbVid, e.UsbVid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(have.UsbPid, e.UsbPid, StringComparison.OrdinalIgnoreCase))
                { exists = true; break; }
            }
            if (!exists) { into.Qmk.Add(e); added++; }
        }
        foreach (var e in imported.E131)
        {
            var exists = false;
            foreach (var have in into.E131)
            {
                if (string.Equals(have.Ip, e.Ip, StringComparison.OrdinalIgnoreCase)
                    && have.StartUniverse == e.StartUniverse)
                { exists = true; break; }
            }
            if (!exists) { into.E131.Add(e); added++; }
        }
        return added;
    }

    private static string Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Int(JsonObject o, string key, int fallback)
    {
        if (o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return fallback;
    }
}
