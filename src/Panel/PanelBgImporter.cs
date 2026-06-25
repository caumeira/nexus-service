using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>
/// Two-phase panel-background import: Stage (upload + server preview) then
/// Commit (crop + bake). The staged raw file is deleted on commit or cancel.
/// No source is retained after commit.
/// </summary>
public static class PanelBgImporter
{
    public const long MaxFileSize = 500L * 1024 * 1024;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif" };
    private static readonly string[] AnimatedExtensions = { ".gif", ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg" };

    /// <summary>
    /// Stores the raw upload and extracts a 1280-wide preview jpg via ffmpeg.
    /// Returns StageResult with stageId on success, or Error on unsupported/unreadable input.
    /// </summary>
    public static async Task<StageResult> StageAsync(
        PanelBgLibrary library,
        string deviceId,
        string sourcePath,
        string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        bool isImage = Array.Exists(ImageExtensions, e => e == ext);
        bool isAnimated = Array.Exists(AnimatedExtensions, e => e == ext);
        if (!isImage && !isAnimated)
        {
            return StageResult.Failure("Unsupported file format");
        }

        if (FfmpegResolver.Path is null)
        {
            return StageResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        library.SweepStaging(deviceId, maxAgeMinutes: 30);

        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var stageId = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        var stagingDir = library.GetStagingDir(deviceId);
        Directory.CreateDirectory(stagingDir);

        var stagedPath = Path.Combine(stagingDir, stageId + ext);
        try
        {
            File.Move(sourcePath, stagedPath);
        }
        catch (Exception ex)
        {
            return StageResult.Failure($"Failed to store upload: {ex.Message}");
        }

        var previewPath = library.GetStagePreviewPath(deviceId, stageId);
        try
        {
            await MediaImporter.RunFfmpeg(
                "-y", "-i", stagedPath,
                "-frames:v", "1",
                "-vf", "scale=1280:1280:force_original_aspect_ratio=decrease",
                "-q:v", "4",
                previewPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-bg-stage] ffmpeg preview failed: {ex.Message}");
        }

        if (!File.Exists(previewPath) || new FileInfo(previewPath).Length == 0)
        {
            try { File.Delete(stagedPath); }
            catch { }
            try { File.Delete(previewPath); }
            catch { }
            return StageResult.Failure("Unsupported or unreadable video");
        }

        return StageResult.Success(stageId);
    }

    /// <summary>
    /// Bakes the staged raw file into a committed asset (cropped + scaled media + thumb),
    /// then deletes the staging files for stageId.
    /// </summary>
    public static async Task<CommitResult> CommitAsync(
        PanelBgLibrary library,
        string deviceId,
        string stageId,
        CropRect crop,
        int targetW,
        int targetH)
    {
        var stagedPath = library.FindStagedRaw(deviceId, stageId);
        if (stagedPath is null || !File.Exists(stagedPath))
        {
            return CommitResult.Failure("Stage not found or expired");
        }

        if (FfmpegResolver.Path is null)
        {
            return CommitResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var originalName = Path.GetFileName(stagedPath);
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var assetId = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");

        bool isImage = Array.Exists(ImageExtensions, e => e == ext);
        bool isAnimated = Array.Exists(AnimatedExtensions, e => e == ext);

        var dir = library.GetItemDir(deviceId, assetId);
        try
        {
            Directory.CreateDirectory(dir);

            if (isAnimated)
            {
                int ew = targetW - (targetW & 1);
                int eh = targetH - (targetH & 1);
                var filter = $"{crop.ToFfmpegCrop()},scale={ew}:{eh}";
                var mediaPath = library.GetMediaPath(deviceId, assetId, ".mp4");
                await MediaImporter.RunFfmpeg("-y", "-i", stagedPath,
                    "-vf", filter,
                    "-r", "30",
                    "-c:v", "libx264", "-profile:v", "main", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart",
                    "-t", "30", mediaPath);

                if (!File.Exists(mediaPath))
                {
                    library.DeleteItem(deviceId, assetId);
                    return CommitResult.Failure("ffmpeg produced no media file");
                }
            }
            else
            {
                var filter = $"{crop.ToFfmpegCrop()},scale={targetW}:{targetH}";
                var mediaPath = library.GetMediaPath(deviceId, assetId, ".jpg");
                await MediaImporter.RunFfmpeg("-y", "-i", stagedPath,
                    "-vf", filter,
                    "-frames:v", "1", "-q:v", "3", mediaPath);

                if (!File.Exists(mediaPath))
                {
                    library.DeleteItem(deviceId, assetId);
                    return CommitResult.Failure("ffmpeg produced no media file");
                }
            }

            var thumbFilter = $"{crop.ToFfmpegCrop()},scale=480:480:force_original_aspect_ratio=decrease";
            var thumbPath = library.GetThumbPath(deviceId, assetId);
            await MediaImporter.RunFfmpeg("-y", "-i", stagedPath,
                "-vf", thumbFilter,
                "-frames:v", "1", "-q:v", "5", thumbPath);

            if (!File.Exists(thumbPath))
            {
                library.DeleteItem(deviceId, assetId);
                return CommitResult.Failure("ffmpeg produced no thumbnail");
            }

            double durationSec = 0;
            if (isAnimated)
            {
                var mediaPath = library.GetMediaPath(deviceId, assetId, ".mp4");
                durationSec = await ProbeDurationSec(mediaPath);
            }

            var item = new PanelBgItem
            {
                Id = assetId,
                Name = Path.GetFileName(originalName),
                Type = isAnimated ? "animated" : "static",
                Width = targetW,
                Height = targetH,
                ImportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DurationSec = durationSec,
            };
            library.SaveMeta(deviceId, item);

            library.DeleteStage(deviceId, stageId);

            return CommitResult.Success(item);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-bg-commit] failed: {ex.Message}");
            library.DeleteItem(deviceId, assetId);
            return CommitResult.Failure(ex.Message);
        }
    }

    // ffmpeg exits non-zero when invoked with only -i (no output), so stderr
    // must be captured with the exit code ignored.
    private static async Task<double> ProbeDurationSec(string path)
    {
        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return 0;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 0;
        }

        var stderr = await stderrTask;
        var m = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success)
        {
            return 0;
        }

        var hours = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    public readonly record struct StageResult(string? StageId, string? Error)
    {
        public bool Ok => StageId is not null;
        public static StageResult Success(string stageId) => new(stageId, null);
        public static StageResult Failure(string error) => new(null, error);
    }

    public readonly record struct CommitResult(PanelBgItem? Item, string? Error)
    {
        public bool Ok => Item is not null;
        public static CommitResult Success(PanelBgItem item) => new(item, null);
        public static CommitResult Failure(string error) => new(null, error);
    }
}
