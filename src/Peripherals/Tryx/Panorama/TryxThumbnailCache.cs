using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Per-device JPEG thumbnail and duration sidecar cache for uploaded custom media.</summary>
public static class TryxThumbnailCache
{
    public static string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "tryx-thumbs");

    public static string ThumbPath(string deviceFileName)
        => Path.Combine(CacheDir, deviceFileName + ".jpg");

    private static string DurPath(string deviceFileName)
        => Path.Combine(CacheDir, deviceFileName + ".dur");

    /// <summary>Derives the on-device filename from a user source name.
    /// Stem chars outside [A-Za-z0-9_.-] become '_'; stem capped at 56 so the
    /// full name (stem + ".mp4") stays at most 60 chars.</summary>
    public static string DeviceFileName(string sourceName)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        var sanitized = sb.ToString();
        if (sanitized.Length > 56) sanitized = sanitized.Substring(0, 56);
        if (string.IsNullOrEmpty(sanitized)) sanitized = "media";
        return sanitized + ".mp4";
    }

    /// <summary>True iff the name is a structurally valid device filename: non-empty,
    /// at most 64 chars, no path separators or "..", only [A-Za-z0-9_.-].</summary>
    public static bool IsSafeDeviceName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return false;
        foreach (var c in name)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                  (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Extracts one frame from <paramref name="sourceMp4"/> into the cache
    /// as a 320-px-wide JPEG. Best-effort: exceptions are swallowed.</summary>
    public static void Write(string ffmpegPath, string sourceMp4, string deviceFileName)
    {
        if (!IsSafeDeviceName(deviceFileName)) return;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var dest = ThumbPath(deviceFileName);
            var args = $"-nostdin -hide_banner -loglevel error -y " +
                       $"-i \"{sourceMp4}\" " +
                       $"-frames:v 1 -vf scale=320:-2 -q:v 4 " +
                       $"\"{dest}\"";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            // Drain pipes to prevent deadlock when the OS buffer fills.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(15_000);
            outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
        }
        catch { /* best effort */ }
    }

    // 512 KiB cap keeps inline data-URLs from bloating the action response.
    private const int MaxThumbBytes = 512 * 1024;

    /// <summary>Returns a data-URL for the cached thumbnail, or null if absent or
    /// too large.</summary>
    public static string? ReadDataUrl(string deviceFileName)
    {
        if (!IsSafeDeviceName(deviceFileName)) return null;
        try
        {
            var path = ThumbPath(deviceFileName);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > MaxThumbBytes) return null;
            var bytes = File.ReadAllBytes(path);
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch { return null; }
    }

    public static void WriteDuration(string deviceFileName, double seconds)
    {
        if (!IsSafeDeviceName(deviceFileName)) return;
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(DurPath(deviceFileName),
                seconds.ToString(CultureInfo.InvariantCulture));
        }
        catch { /* best effort */ }
    }

    public static double ReadDuration(string deviceFileName)
    {
        if (!IsSafeDeviceName(deviceFileName)) return 0;
        try
        {
            var path = DurPath(deviceFileName);
            if (!File.Exists(path)) return 0;
            var text = File.ReadAllText(path).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    public static void Delete(string deviceFileName)
    {
        if (!IsSafeDeviceName(deviceFileName)) return;
        try { File.Delete(ThumbPath(deviceFileName)); }
        catch { /* best effort */ }
        try { File.Delete(DurPath(deviceFileName)); }
        catch { /* best effort */ }
    }

    // HH:MM:SS.ss from ffmpeg -i stderr when ffprobe is absent.
    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Reads clip duration in seconds. Prefers ffprobe (same dir as ffmpeg);
    /// falls back to ffmpeg -i stderr regex. Returns 0 on any failure.</summary>
    public static double ProbeDuration(string ffmpegPath, string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
            var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            var ffprobePath = Path.Combine(dir, ffprobeName);
            if (File.Exists(ffprobePath))
            {
                return ProbeDurationViaFfprobe(ffprobePath, filePath);
            }
            return ProbeDurationViaFfmpegStderr(ffmpegPath, filePath);
        }
        catch { return 0; }
    }

    private static double ProbeDurationViaFfprobe(string ffprobePath, string filePath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{filePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return 0;
            var errTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            errTask.GetAwaiter().GetResult();
            return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static double ProbeDurationViaFfmpegStderr(string ffmpegPath, string filePath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                // No -loglevel: the Duration line appears in default-level stderr output.
                Arguments = $"-i \"{filePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return 0;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            outTask.GetAwaiter().GetResult();
            var m = DurationRegex.Match(stderr);
            if (!m.Success) return 0;
            var h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return h * 3600 + min * 60 + sec;
        }
        catch { return 0; }
    }
}
