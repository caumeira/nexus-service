using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class TryxOverlaySnapshot
{
    public string[] Stats { get; set; } = [];
    public string Color { get; set; } = "";
    public string Align { get; set; } = "";
}

public sealed class TryxStatusResponse
{
    public bool Connected { get; set; }
    public TryxPanoramaState? State { get; set; }
    public TryxOverlaySnapshot? Overlay { get; set; }
}

public sealed class TryxMediaImportResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public string? Media { get; set; }
}

public sealed class TryxPresetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class TryxPresetListResponse
{
    public List<TryxPresetItem> Presets { get; set; } = new();
}

public sealed class TryxMediaItem
{
    public string Name { get; set; } = "";
    public string? Thumb { get; set; }
    public double DurationSec { get; set; }
}

public sealed class TryxMediaListResponse
{
    public List<TryxMediaItem> Media { get; set; } = new();
}

/// <summary>Generic ack body for Tryx control routes.</summary>
public sealed class TryxAckResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
}

public sealed class TryxEnableRequest
{
    public bool Enable { get; set; }
}

public sealed class TryxBrightnessRequest
{
    public int Value { get; set; }
}

public sealed class TryxFanRequest
{
    public string Mode { get; set; } = "";
    public int? Fixed { get; set; }
    public int[][]? Curve { get; set; }
}

public sealed class TryxPresetRequest
{
    public string Id { get; set; } = "";
}

public sealed class TryxMediaSelectRequest
{
    public string Name { get; set; } = "";
}

public sealed class TryxMediaDeleteRequest
{
    public string Name { get; set; } = "";
}

public sealed class TryxOverlayRequest
{
    public string[] Stats { get; set; } = [];
    public string Color { get; set; } = "";
    public string Align { get; set; } = "";
    public string? Filter { get; set; }
    public int? Opacity { get; set; }
}

/// <summary>
/// First-party Tryx Panorama control routes. Calls into the shared
/// <see cref="TryxPanoramaHub"/> singleton; the dashboard device page talks to
/// these directly (no SDK dispatch layer).
/// </summary>
public static class TryxRoutes
{
    // The panel's built-in wallpapers, ordered so index N maps to default_{N+1}
    // (TryxRkProtocol.PresetMediaFile). Names mirror Kanali's catalog for the RK
    // firmware; the panel cannot enumerate them, so this list is the source of truth.
    private static readonly (string Id, string Name)[] KnownPresets =
    {
        ("default_01", "Sally"),
        ("default_02", "Otterly Dissapointed"),
        ("default_03", "BFFs"),
        ("default_04", "Lazybara"),
        ("default_05", "Scarlet Panda"),
        ("default_06", "B00/CHI"),
        ("default_07", "BOOCHI Graffiti"),
        ("default_08", "Stomp It!"),
        ("default_09", "Metamorphosis"),
        ("default_10", "Evolution"),
        ("default_11", "D.C.I.Bubbles"),
        ("default_12", "Sleepless"),
        ("default_13", "Rover"),
        ("default_14", "Drowsy"),
        ("default_15", "PinkEmpress"),
        ("default_16", "Dancing King Boochi"),
        ("default_17", "Eyes 1"),
        ("default_18", "Eyes 2"),
        ("default_19", "Eyes 3"),
        ("default_20", "Eyes 4"),
        ("default_21", "Eyes 5"),
    };

    public static void MapTryxEndpoints(this WebApplication app)
    {
        app.MapGet("/tryx/status", (TryxPanoramaHub hub) =>
        {
            var ov = hub.Overlay;
            var resp = new TryxStatusResponse
            {
                Connected = hub.IsConnected,
                State = hub.State,
                Overlay = new TryxOverlaySnapshot
                {
                    Stats = ov.Stats,
                    Color = ov.Color,
                    Align = ov.Align,
                },
            };
            return Results.Json(resp, AppJsonContext.Default.TryxStatusResponse);
        });

        app.MapPost("/tryx/enable", (TryxEnableRequest body, TryxPanoramaHub hub) =>
        {
            var ok = hub.SetEnabled(body.Enable);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/brightness", (TryxBrightnessRequest body, TryxPanoramaHub hub) =>
        {
            if (body.Value < 0 || body.Value > 100)
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "value must be 0..100" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SetBrightness(body.Value);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/fan", (TryxFanRequest body, TryxPanoramaHub hub) =>
        {
            if (body.Mode == "fixed")
            {
                var pct = body.Fixed ?? 40;
                var ok = hub.SetFanFixed(Math.Clamp(pct, 0, 100));
                return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
            }
            if (body.Mode == "smart")
            {
                var curve = body.Curve;
                if (curve is { Length: > 0 })
                {
                    foreach (var pt in curve)
                    {
                        if (pt is not { Length: 2 })
                        {
                            return Results.Json(
                                new TryxAckResponse { Ok = false, Msg = "invalid curve; expected [[temp,duty],...]" },
                                AppJsonContext.Default.TryxAckResponse);
                        }
                    }
                }
                var ok = hub.SetFanSmart(curve is { Length: > 0 } ? curve : null);
                return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
            }
            return Results.Json(
                new TryxAckResponse { Ok = false, Msg = "mode must be 'smart' or 'fixed'" },
                AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/presets", () =>
        {
            var resp = new TryxPresetListResponse();
            foreach (var (id, name) in KnownPresets)
            {
                resp.Presets.Add(new TryxPresetItem { Id = id, Name = name });
            }
            return Results.Json(resp, AppJsonContext.Default.TryxPresetListResponse);
        });

        app.MapPost("/tryx/preset", (TryxPresetRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Id))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing id" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var index = Array.FindIndex(KnownPresets, p => p.Id == body.Id);
            if (index < 0)
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "unknown preset" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SetPreset(TryxRkProtocol.PresetMediaFile(index + 1));
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/media", (TryxPanoramaHub hub) =>
        {
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
            return Results.Json(resp, AppJsonContext.Default.TryxMediaListResponse);
        });

        app.MapPost("/tryx/media/select", (TryxMediaSelectRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SelectCustomMedia(body.Name);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/media/delete", (TryxMediaDeleteRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            if (!TryxThumbnailCache.IsSafeDeviceName(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "invalid name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ack = DeleteMediaFile(hub, body.Name);
            return Results.Json(ack, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/overlay", (TryxOverlayRequest body, TryxPanoramaHub hub) =>
        {
            var stats = new List<string>();
            foreach (var s in body.Stats ?? [])
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                stats.Add(s);
                if (stats.Count >= 3) break;
            }
            var cfg = new TryxOverlayConfig
            {
                Stats = stats.ToArray(),
                Color = string.IsNullOrWhiteSpace(body.Color) ? "#ffffff" : body.Color,
                Align = string.IsNullOrWhiteSpace(body.Align) ? "Center" : body.Align,
                Filter = string.IsNullOrWhiteSpace(body.Filter) ? null : body.Filter,
                Opacity = body.Opacity is null ? 100 : Math.Clamp(body.Opacity.Value, 0, 100),
            };
            var ok = hub.SetOverlay(cfg);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/media/file", async (string? name, TryxPanoramaHub hub, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(name) || !TryxThumbnailCache.IsSafeDeviceName(name))
            {
                return Results.NotFound();
            }

            if (TryxMediaStore.Exists(name))
            {
                return Results.File(TryxMediaStore.Path(name), "video/mp4", enableRangeProcessing: true);
            }

            if (!hub.IsConnected || string.IsNullOrEmpty(hub.State.AdbSerial))
            {
                return Results.NotFound();
            }

            try
            {
                var pulled = await hub.EnsureLocalCopyAsync(name, ct);
                if (!pulled)
                {
                    return Results.NotFound();
                }
                return Results.File(TryxMediaStore.Path(name), "video/mp4", enableRangeProcessing: true);
            }
            catch (Exception)
            {
                return Results.NotFound();
            }
        }).DisableAntiforgery();

        app.MapPost("/tryx/media", async (HttpContext ctx, TryxPanoramaHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            if (!hub.IsConnected)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "Tryx Panorama not connected" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            // Optional crop ("x,y,w,h" normalized 0..1) + target size from the dashboard
            // cropper; applied in the transcode. Defaults to the panel's native 2:1 surface, no crop.
            var crop = ParseCrop(form["crop"].ToString());
            var targetW = ParseInt(form["targetWidth"].ToString(), 858);
            var targetH = ParseInt(form["targetHeight"].ToString(), 428);

            var ext = Path.GetExtension(file.FileName);
            var tempInput = Path.Combine(Path.GetTempPath(), $"nexus-tryx-in-{Guid.NewGuid()}{ext}");
            try
            {
                using (var s = File.Create(tempInput))
                {
                    await file.CopyToAsync(s);
                }
                var ok = await hub.ImportAndPlayVideoAsync(tempInput, file.FileName, crop, targetW, targetH, ctx.RequestAborted);
                if (!ok)
                {
                    return Results.Json(
                        new TryxMediaImportResponse { Error = true, Msg = "Import failed (transcode, push, or verification error)" },
                        AppJsonContext.Default.TryxMediaImportResponse);
                }
                return Results.Json(
                    new TryxMediaImportResponse { Media = hub.State.CurrentMedia },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            finally
            {
                try { File.Delete(tempInput); } catch { /* best effort */ }
            }
        }).DisableAntiforgery();
    }

    private static TryxVideoCrop? ParseCrop(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length != 4) return null;
        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return new TryxVideoCrop(x, y, w, h);
        }
        return null;
    }

    private static int ParseInt(string raw, int fallback)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

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
                if (trimmed.Length == 0) continue;
                // The panel's pcMedia dir is shared with any tool that ever drove
                // it (e.g. Kanali), which leaves non-video files behind. The app
                // only plays video, so list video files only.
                var lower = trimmed.ToLowerInvariant();
                if (lower.EndsWith(".mp4") || lower.EndsWith(".mov") || lower.EndsWith(".webm")
                    || lower.EndsWith(".mkv") || lower.EndsWith(".m4v") || lower.EndsWith(".avi"))
                {
                    files.Add(trimmed);
                }
            }
        }
        catch { /* adb unavailable */ }
        return files;
    }

    private static TryxAckResponse DeleteMediaFile(TryxPanoramaHub hub, string name)
    {
        var adbSerial = hub.State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            return new TryxAckResponse { Ok = false, Msg = "no adb serial" };
        }
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return new TryxAckResponse { Ok = false, Msg = "adb not found" };
        }
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
            if (p is null)
            {
                return new TryxAckResponse { Ok = false, Msg = "failed to start adb" };
            }
            p.WaitForExit(8_000);
            if (p.ExitCode == 0)
            {
                TryxThumbnailCache.Delete(name);
            }
            return new TryxAckResponse { Ok = p.ExitCode == 0 };
        }
        catch (Exception ex)
        {
            return new TryxAckResponse { Ok = false, Msg = ex.Message };
        }
    }
}
