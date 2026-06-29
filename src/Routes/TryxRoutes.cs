using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class TryxStatusResponse
{
    public bool Connected { get; set; }
    public TryxPanoramaState? State { get; set; }
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

/// <summary>
/// The Tryx control surface rides the SDK dispatch path (see TryxActions); the only
/// route is the media upload, which the host-mediated MediaImport component posts to
/// (a sandboxed app cannot upload a file or reach loopback itself).
/// </summary>
public static class TryxRoutes
{
    public static void MapTryxEndpoints(this WebApplication app)
    {
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
}
