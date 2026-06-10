using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Gallery;

/// <summary>
/// Disk-cached JPEG thumbnails for gallery items, generated with the bundled
/// ffmpeg. The cache file name embeds the source mtime so edited images
/// regenerate naturally; stale variants of an item are pruned after a write.
/// Returns null when ffmpeg is unavailable — callers 404 and the UI falls
/// back to a placeholder.
/// </summary>
public static class GalleryThumbnails
{
    public const int MaxEdge = 480;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    // Negative cache: id → source mtime ticks of the last failed generation.
    // Without it an undecodable image re-spawns ffmpeg on every grid render;
    // an edit to the file (new mtime) retries naturally.
    private static readonly ConcurrentDictionary<string, long> FailedAtMtime = new();

    public static async Task<string?> GetOrCreateAsync(GalleryLibrary library, string id, string sourcePath)
    {
        if (FfmpegResolver.Path is null)
            return null;

        DateTime mtimeUtc;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                return null;
            mtimeUtc = info.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }

        var thumbPath = Path.Combine(library.ThumbsDir, $"{id}-{mtimeUtc.Ticks:x}.jpg");
        if (File.Exists(thumbPath))
            return thumbPath;

        if (FailedAtMtime.TryGetValue(id, out var failedTicks) && failedTicks == mtimeUtc.Ticks)
            return null;

        var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (File.Exists(thumbPath))
                return thumbPath;

            Directory.CreateDirectory(library.ThumbsDir);
            var tmp = thumbPath + ".tmp.jpg";
            await MediaImporter.RunFfmpeg("-y", "-i", sourcePath,
                "-vf", $"scale={MaxEdge}:{MaxEdge}:force_original_aspect_ratio=decrease",
                "-frames:v", "1", "-q:v", "5", tmp);
            if (!File.Exists(tmp))
            {
                FailedAtMtime[id] = mtimeUtc.Ticks;
                return null;
            }

            File.Move(tmp, thumbPath, overwrite: true);
            FailedAtMtime.TryRemove(id, out _);
            PruneStaleVariants(library.ThumbsDir, id, thumbPath);
            return thumbPath;
        }
        catch
        {
            FailedAtMtime[id] = mtimeUtc.Ticks;
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void PruneStaleVariants(string thumbsDir, string id, string keepPath)
    {
        try
        {
            foreach (var file in Directory.GetFiles(thumbsDir, $"{id}-*.jpg"))
            {
                if (!string.Equals(file, keepPath, StringComparison.Ordinal))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort */ }
    }
}
