using System;
using System.Diagnostics;
using System.IO;

namespace Nexus.Service.Platform;

/// <summary>
/// Single source of truth for locating the ffmpeg binary at runtime. The
/// service ships ffmpeg.exe bundled next to the AOT output on Windows, and
/// falls back to the Homebrew default on macOS. All ffmpeg callers should go
/// through this instead of hardcoding a path -- that way when the bundle
/// location changes, one edit fixes every feature (media import, screen
/// mirror, etc.).
/// </summary>
public static class FfmpegResolver
{
    private static string? _cachedPath;

    public static string? Path
    {
        get
        {
            if (_cachedPath is not null)
            {
                return _cachedPath;
            }
            _cachedPath = Locate();
            return _cachedPath;
        }
    }

    private static string? Locate()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "ffmpeg.exe",
            }
            : new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg"),
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg",
                "ffmpeg",
            };
        foreach (var path in candidates)
        {
            if (Probe(path))
            {
                return path;
            }
        }
        return null;
    }

    private static bool Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
