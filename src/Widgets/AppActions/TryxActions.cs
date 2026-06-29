using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host actions for the Tryx Panorama device-app: status, screen on/off,
/// brightness, fan control, preset selection, and media listing/deletion. The
/// sandboxed app cannot loopback-fetch, so all control rides the dispatch path;
/// only the media upload is a route (the host-mediated MediaImport posts to it).</summary>
public static class TryxActions
{
    // Fixed preset set; the device cannot enumerate its presets over serial.
    // Matches Kanali's waterBlockScreenList; Id is sent verbatim to waterBlockScreen.
    private static readonly (string Id, string Name)[] KnownPresets =
    {
        ("Pre-set 1: Cooling delivery", "Cooling delivery"),
        ("Pre-set 2: Migration", "Migration"),
        ("Pre-set 3: Quantum time capsule", "Quantum time capsule"),
        ("Pre-set 4: Exo-Ecologies", "Exo-Ecologies"),
        ("Pre-set 5: Racing", "Racing"),
        ("Pre-set 6: Shuttle", "Shuttle"),
        ("Pre-set 7: Gift of TRYX", "Gift of TRYX"),
    };

    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("tryx.status", (services, _, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var resp = new TryxStatusResponse { Connected = hub.IsConnected, State = hub.State };
            var json = JsonSerializer.Serialize(resp, AppJsonContext.Default.TryxStatusResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("tryx.setEnabled", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            bool enable = true;
            if (args != null && args.TryGetValue("enable", out var el))
            {
                enable = el.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => el.GetDouble() != 0,
                    JsonValueKind.String => string.Equals(el.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                    _ => true,
                };
            }
            return Task.FromResult<JsonElement?>(Ack(hub.SetEnabled(enable)));
        });

        registry.Register("tryx.setBrightness", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var v = Num(args, "value");
            if (v is null || v < 0 || v > 100)
                return Task.FromResult<JsonElement?>(Ack(false, "value must be 0..100"));
            return Task.FromResult<JsonElement?>(Ack(hub.SetBrightness((int)v.Value)));
        });

        registry.Register("tryx.setFan", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var mode = Str(args, "mode");
            if (mode == "fixed")
            {
                var pct = Num(args, "fixed") ?? 40;
                return Task.FromResult<JsonElement?>(Ack(hub.SetFanFixed((int)Math.Clamp(pct, 0, 100))));
            }
            if (mode == "smart")
            {
                var curve = ParseCurve(args);
                if (curve is null)
                    return Task.FromResult<JsonElement?>(Ack(false, "invalid curve; expected [[temp,duty],...]"));
                return Task.FromResult<JsonElement?>(Ack(hub.SetFanSmart(curve.Length > 0 ? curve : null)));
            }
            return Task.FromResult<JsonElement?>(Ack(false, "mode must be 'smart' or 'fixed'"));
        });

        registry.Register("tryx.listPresets", (_, _, _) =>
        {
            var resp = new TryxPresetListResponse();
            foreach (var (id, name) in KnownPresets)
            {
                resp.Presets.Add(new TryxPresetItem { Id = id, Name = name });
            }
            var json = JsonSerializer.Serialize(resp, AppJsonContext.Default.TryxPresetListResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("tryx.setPreset", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var id = Str(args, "id");
            if (string.IsNullOrEmpty(id))
                return Task.FromResult<JsonElement?>(Ack(false, "missing id"));
            return Task.FromResult<JsonElement?>(Ack(hub.SetPreset(id!)));
        });

        registry.Register("tryx.listMedia", (services, _, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var names = ListMediaFiles(hub);
            var resp = new TryxMediaListResponse();
            foreach (var name in names)
            {
                resp.Media.Add(new TryxMediaItem
                {
                    Name = name,
                    Thumb = TryxThumbnailCache.ReadDataUrl(name),
                    DurationSec = TryxThumbnailCache.ReadDuration(name),
                });
            }
            var json = JsonSerializer.Serialize(resp, AppJsonContext.Default.TryxMediaListResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("tryx.selectMedia", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var name = Str(args, "name");
            if (string.IsNullOrEmpty(name))
                return Task.FromResult<JsonElement?>(Ack(false, "missing name"));
            return Task.FromResult<JsonElement?>(Ack(hub.SelectCustomMedia(name!)));
        });

        registry.Register("tryx.deleteMedia", (services, args, _) =>
        {
            var hub = services.GetRequiredService<TryxPanoramaHub>();
            var name = Str(args, "name");
            if (string.IsNullOrEmpty(name))
                return Task.FromResult<JsonElement?>(Ack(false, "missing name"));
            if (!TryxThumbnailCache.IsSafeDeviceName(name!))
                return Task.FromResult<JsonElement?>(Ack(false, "invalid name"));
            var adbSerial = hub.State.AdbSerial;
            if (string.IsNullOrEmpty(adbSerial))
                return Task.FromResult<JsonElement?>(Ack(false, "no adb serial"));
            var adbPath = AdbLocator.ResolveAdbPath();
            if (adbPath is null)
                return Task.FromResult<JsonElement?>(Ack(false, "adb not found"));
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"-s {adbSerial} shell rm /sdcard/pcMedia/{name}",
                    WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p is null) return Task.FromResult<JsonElement?>(Ack(false, "failed to start adb"));
                p.WaitForExit(8_000);
                if (p.ExitCode == 0)
                {
                    TryxThumbnailCache.Delete(name!);
                }
                return Task.FromResult<JsonElement?>(Ack(p.ExitCode == 0));
            }
            catch (Exception ex)
            {
                return Task.FromResult<JsonElement?>(Ack(false, ex.Message));
            }
        });
    }

    private static List<string> ListMediaFiles(TryxPanoramaHub hub)
    {
        var files = new List<string>();
        var adbSerial = hub.State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial)) return files;
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null) return files;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {adbSerial} shell ls /sdcard/pcMedia/",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return files;
            p.WaitForExit(8_000);
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed)) files.Add(trimmed);
            }
        }
        catch { /* adb unavailable */ }
        return files;
    }

    private static int[][]? ParseCurve(Dictionary<string, JsonElement>? args)
    {
        if (args is null || !args.TryGetValue("curve", out var el)) return Array.Empty<int[]>();
        if (el.ValueKind != JsonValueKind.Array) return null;
        var result = new List<int[]>();
        foreach (var point in el.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array) return null;
            var pts = new List<int>();
            foreach (var n in point.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Number) return null;
                pts.Add(n.GetInt32());
            }
            if (pts.Count != 2) return null;
            result.Add(pts.ToArray());
        }
        return result.ToArray();
    }

}
