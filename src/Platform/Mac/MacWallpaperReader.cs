using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Resolves the current desktop wallpaper for the dashboard's "wallpaper"
/// background mode (the web blurs + veils it), the macOS counterpart to
/// <see cref="WallpaperReader"/> on Windows.
///
/// macOS keeps the chosen picture in a per-version store (the legacy
/// desktoppicture.db is empty on Sonoma+); NSWorkspace.desktopImageURLForScreen:
/// reads the active wallpaper URL across all versions in-process, with no
/// Automation/TCC prompt. The system pictures are HEIC, which the dashboard's
/// createImageBitmap can't rely on decoding (and the originals are ~25 MB), so
/// anything that isn't already JPEG/PNG is transcoded to a capped JPEG via sips.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacWallpaperReader
{
    private const string Libobjc = "/usr/lib/libobjc.dylib";

    /// <summary>
    /// A web-decodable image file for the current wallpaper, or null when none
    /// is set / resolution fails (the route then 404s and the web shows the base).
    /// </summary>
    public static (string Path, string ContentType)? GetServable()
    {
        var src = GetCurrentWallpaperPath();
        if (src is null || !File.Exists(src)) return null;

        var ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg") return (src, "image/jpeg");
        if (ext is ".png") return (src, "image/png");

        var jpeg = TranscodeToJpeg(src);
        return jpeg is null ? null : (jpeg, "image/jpeg");
    }

    /// <summary>NSWorkspace.desktopImageURLForScreen: → the wallpaper file path.</summary>
    public static string? GetCurrentWallpaperPath()
    {
        try
        {
            IntPtr nsWorkspace = objc_getClass("NSWorkspace");
            if (nsWorkspace == IntPtr.Zero) return null;
            IntPtr shared = MsgSend(nsWorkspace, sel_registerName("sharedWorkspace"));
            if (shared == IntPtr.Zero) return null;

            // desktopImageURLForScreen: takes the screen to read; mainScreen is
            // the one the window opens on. nil is a valid "any screen" argument
            // when there's no main screen (e.g. a fully headless session).
            IntPtr nsScreen = objc_getClass("NSScreen");
            IntPtr mainScreen = nsScreen == IntPtr.Zero
                ? IntPtr.Zero
                : MsgSend(nsScreen, sel_registerName("mainScreen"));

            IntPtr url = MsgSend(shared, sel_registerName("desktopImageURLForScreen:"), mainScreen);
            if (url == IntPtr.Zero) return null;

            IntPtr nsPath = MsgSend(url, sel_registerName("path"));
            if (nsPath == IntPtr.Zero) return null;
            IntPtr utf8 = MsgSend(nsPath, sel_registerName("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Transcode to JPEG via sips, cached by source write-time so a wallpaper
    /// change re-renders but repeat fetches of the same image reuse the file.
    /// </summary>
    private static string? TranscodeToJpeg(string src)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "nexus-wallpaper");
            Directory.CreateDirectory(cacheDir);
            var stamp = File.GetLastWriteTimeUtc(src).Ticks;
            var outPath = Path.Combine(cacheDir, $"wp-{stamp}.jpg");
            if (File.Exists(outPath)) return outPath;

            // sips ships with macOS. -Z caps the long edge: the web blurs the
            // image down to a 16 px texture anyway, so full resolution is wasted
            // bytes over the socket.
            var psi = new ProcessStartInfo("/usr/bin/sips")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("format");
            psi.ArgumentList.Add("jpeg");
            psi.ArgumentList.Add("-Z");
            psi.ArgumentList.Add("1600");
            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outPath);

            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && File.Exists(outPath) ? outPath : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);
}
