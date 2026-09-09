using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// A crop rectangle in source-normalized coordinates (0..1) plus an optional
/// orientation (mirror + CW rotation), applied before the scale-to-panel step.
/// Renders to an ffmpeg orientation-then-crop filter that resolves against the
/// oriented frame's dimensions (<c>iw</c>/<c>ih</c> after any transpose).
/// </summary>
public readonly record struct Slv3LcdCropRect(double X, double Y, double W, double H, int Rotate = 0, bool Mirror = false)
{
    /// <summary>Full frame with no orientation: nothing to apply before scaling.</summary>
    public bool IsIdentity => X <= 0 && Y <= 0 && W >= 1 && H >= 1 && Rotate == 0 && !Mirror;

    public string ToFfmpegFilter() => string.Create(
        CultureInfo.InvariantCulture,
        $"{CropRect.OrientationFilter(Rotate, Mirror)}crop=iw*{W}:ih*{H}:iw*{X}:ih*{Y}");
}

/// <summary>
/// Encodes an arbitrary source image to a 400x400 baseline JPEG for the
/// SL-LCD Wireless screen, via the bundled ffmpeg subprocess (no image
/// library dependency).
/// </summary>
public static class Slv3LcdImage
{
    public const int PanelWidth = Slv3LcdProtocol.PanelWidth;
    public const int PanelHeight = Slv3LcdProtocol.PanelHeight;

    /// <summary>Firmware JPEG size cap (GetJpgBytes, roughly 100 KB).</summary>
    public const int MaxJpegBytes = 100000;

    // mjpeg -q:v: 2=best..31=worst. Starts near "90/100" quality and backs off
    // toward smaller output only if the cap is exceeded.
    private const int InitialQuality = 3;
    private const int QualityStep = 5;
    private const int MaxQuality = 31;
    private const int MaxAttempts = 5;

    /// <summary>
    /// Encodes <paramref name="sourceBytes"/> (an arbitrary image file's raw
    /// bytes) to a 400x400 JPEG. <paramref name="sourceExtension"/> (with the
    /// leading dot, e.g. ".png") selects the input format for ffmpeg.
    /// </summary>
    public static async Task<Slv3LcdEncodeResult> EncodeAsync(
        byte[] sourceBytes, string sourceExtension, Slv3LcdCropRect? crop = null)
    {
        if (sourceBytes.Length == 0)
        {
            return Slv3LcdEncodeResult.Failure("Empty source image.");
        }
        if (FfmpegResolver.Path is null)
        {
            return Slv3LcdEncodeResult.Failure("LCD image encode requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "nexus-lianli-lcd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var sourcePath = Path.Combine(workDir, "source" + sourceExtension);
        var outputPath = Path.Combine(workDir, "frame.jpg");

        try
        {
            await File.WriteAllBytesAsync(sourcePath, sourceBytes).ConfigureAwait(false);

            var scaleFilter =
                $"scale={PanelWidth}:{PanelHeight}:force_original_aspect_ratio=decrease,pad={PanelWidth}:{PanelHeight}:-1:-1:color=black";
            var filter = crop is { IsIdentity: false } c ? $"{c.ToFfmpegFilter()},{scaleFilter}" : scaleFilter;

            var quality = InitialQuality;
            byte[]? smallest = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                await MediaImporter.RunFfmpeg(
                    "-y", "-i", sourcePath,
                    "-vf", filter,
                    "-frames:v", "1",
                    "-q:v", quality.ToString(),
                    outputPath).ConfigureAwait(false);

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    return Slv3LcdEncodeResult.Failure("ffmpeg produced no output.");
                }

                var bytes = await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false);
                if (smallest is null || bytes.Length < smallest.Length)
                {
                    smallest = bytes;
                }
                if (bytes.Length <= MaxJpegBytes)
                {
                    return Slv3LcdEncodeResult.Success(bytes);
                }
                quality = Math.Min(MaxQuality, quality + QualityStep);
            }

            if (smallest is null)
            {
                return Slv3LcdEncodeResult.Failure("ffmpeg produced no output.");
            }
            if (smallest.Length > MaxJpegBytes)
            {
                ServiceLog.Warn(
                    $"[lianli-wireless-lcd] encode still {smallest.Length} bytes after {MaxAttempts} quality attempts, over the {MaxJpegBytes}-byte cap");
            }
            return Slv3LcdEncodeResult.Success(smallest);
        }
        catch (Exception ex)
        {
            return Slv3LcdEncodeResult.Failure(ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}

public readonly record struct Slv3LcdEncodeResult(byte[]? JpegBytes, string? Error)
{
    public bool Ok => JpegBytes is not null;
    public static Slv3LcdEncodeResult Success(byte[] jpeg) => new(jpeg, null);
    public static Slv3LcdEncodeResult Failure(string error) => new(null, error);
}
