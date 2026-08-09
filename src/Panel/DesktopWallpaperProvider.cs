using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

/// <summary>
/// Resolves the console user's current desktop wallpaper file for panel
/// backgrounds. On Windows the service runs as LocalSystem, whose own profile
/// has no wallpaper - resolution goes through the active console user's
/// profile, never HKCU (the LocalSystem HKCU is the SYSTEM hive). macOS
/// resolution lives in <see cref="MacDesktopWallpaperProvider"/>, Linux in
/// <see cref="LinuxDesktopWallpaperProvider"/>.
/// </summary>
public static class DesktopWallpaperProvider
{
    public static string? TryResolve(int width, int height)
    {
        // macOS has no per-monitor crop cache; the client cover-fits the one
        // still WallpaperAgent rendered for the active wallpaper.
        if (OperatingSystem.IsMacOS()) return MacDesktopWallpaperProvider.TryResolve();
        // Linux has no per-monitor crop cache either - GNOME/KDE both resolve
        // a single desktop-wide wallpaper file. Compile-removed on non-Linux
        // RIDs (references LinuxSession), so the reference is LINUX-gated too.
#if LINUX
        if (OperatingSystem.IsLinux()) return LinuxDesktopWallpaperProvider.TryResolve();
#endif

        var themes = ResolveThemesDir();
        if (themes is null) return null;
        // The shell caches a per-monitor crop of that monitor's own wallpaper
        // as CachedFiles\CachedImage_{w}_{h}_POS{n}.jpg. Prefer the crop
        // nearest the requested resolution (client-side DPI rounding can land
        // a few px off the native mode); ties go to the newest write. Beyond
        // the tolerance, serve the full transcoded image and let the client
        // cover-fit it - the same crop the shell's default Fill style makes.
        if (width > 0 && height > 0)
        {
            var cached = Path.Combine(themes, "CachedFiles");
            if (Directory.Exists(cached))
            {
                string? best = null;
                var bestScore = int.MaxValue;
                var bestWrite = DateTime.MinValue;
                foreach (var f in Directory.EnumerateFiles(cached, "CachedImage_*.jpg"))
                {
                    var parts = Path.GetFileNameWithoutExtension(f).Split('_');
                    if (parts.Length < 4
                        || !int.TryParse(parts[1], out var w)
                        || !int.TryParse(parts[2], out var h))
                    {
                        continue;
                    }
                    var score = Math.Abs(w - width) + Math.Abs(h - height);
                    var write = File.GetLastWriteTimeUtc(f);
                    if (score < bestScore || (score == bestScore && write > bestWrite))
                    {
                        bestScore = score;
                        bestWrite = write;
                        best = f;
                    }
                }
                if (best is not null && bestScore <= 8) return best;
            }
        }
        var transcoded = Path.Combine(themes, "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    /// <summary>Directory whose writes mean the wallpaper changed: the shell's
    /// Themes dir on Windows, the wallpaper store on macOS.</summary>
    internal static string? ResolveWatchDir()
    {
        if (OperatingSystem.IsMacOS())
        {
            var store = MacDesktopWallpaperProvider.StoreDir();
            return Directory.Exists(store) ? store : null;
        }
#if LINUX
        if (OperatingSystem.IsLinux()) return LinuxDesktopWallpaperProvider.WatchDir();
#endif
        return ResolveThemesDir();
    }

    /// <summary>True when a write to <paramref name="name"/> under the watched
    /// directory changes what the endpoint serves.</summary>
    internal static bool IsServedFile(string? name)
    {
        var file = name is null ? "" : Path.GetFileName(name);
        if (OperatingSystem.IsMacOS()) return file.Equals("Index.plist", StringComparison.OrdinalIgnoreCase);
#if LINUX
        if (OperatingSystem.IsLinux()) return LinuxDesktopWallpaperProvider.IsServedFile(file);
#endif
        return file.Equals("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("CachedImage_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No-op except on Linux, where TryResolve caches its result;
    /// called from the same debounce signal that broadcasts the change so a
    /// client refetch never hits a stale cache entry.</summary>
    internal static void InvalidateCache()
    {
#if LINUX
        if (OperatingSystem.IsLinux()) LinuxDesktopWallpaperProvider.InvalidateCache();
#endif
    }

    internal static string? ResolveThemesDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var user = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(user)) return null;
        // Profile folder == WTS username holds for local accounts; renamed or
        // collision-suffixed profiles resolve to null and the endpoint 404s
        // (the panel keeps its theme backdrop). ProfileList\{SID} is the exact
        // source if that population ever matters.
        var dir = Path.Combine(@"C:\Users", user, @"AppData\Roaming\Microsoft\Windows\Themes");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string ResolveActiveConsoleUsername()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return string.Empty;
        var buf = IntPtr.Zero;
        try
        {
            // WTSUserName = 5
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5, out buf, out _) || buf == IntPtr.Zero)
                return string.Empty;
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}

/// <summary>
/// Broadcasts <see cref="PanelTopics.DesktopWallpaper"/> when the console
/// user's wallpaper changes. The watched directory only resolves once a user
/// is logged on, and can move on a console-user switch, so every poll pass
/// re-resolves and re-arms when the dir changed or the watcher faulted
/// (FileSystemWatcher stops raising events permanently after an Error).
/// </summary>
public sealed class DesktopWallpaperWatcher : BackgroundService
{
    private readonly MultiplexHub _hub;
    private FileSystemWatcher? _watcher;
    private string? _watchedDir;
    private volatile bool _watcherFaulted;
    private Timer? _debounce;

    public DesktopWallpaperWatcher(MultiplexHub hub)
    {
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        _debounce = new Timer(_ =>
        {
            DesktopWallpaperProvider.InvalidateCache();
            PanelTopics.BroadcastDesktopWallpaper(_hub);
        });
        while (!stoppingToken.IsCancellationRequested)
        {
            var watchDir = DesktopWallpaperProvider.ResolveWatchDir();
            if (watchDir is not null
                && (_watcher is null || _watcherFaulted
                    || !string.Equals(_watchedDir, watchDir, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    _watcher?.Dispose();
                    _watcher = Arm(watchDir);
                    _watchedDir = watchDir;
                    _watcherFaulted = false;
                    ServiceLog.Info($"[wallpaper-watch] armed on {watchDir}");
                }
                catch (Exception ex)
                {
                    _watcher = null;
                    ServiceLog.Warn($"[wallpaper-watch] arm failed: {ex.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private FileSystemWatcher Arm(string watchDir)
    {
        var watcher = new FileSystemWatcher(watchDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        watcher.Changed += (_, e) => OnWatchedMutated(e.Name);
        watcher.Created += (_, e) => OnWatchedMutated(e.Name);
        watcher.Renamed += (_, e) => OnWatchedMutated(e.Name);
        watcher.Error += (_, _) => { _watcherFaulted = true; };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    // Only the files the endpoint serves re-arm the debounce. A theme change
    // also writes slideshow/theme metadata for several seconds; keying the
    // trailing edge on ALL Themes-dir events postponed the broadcast well past
    // the wallpaper write and made the panel lag the desktop (bench-hit).
    // Filtered to the served files, the trailing window only has to outlast
    // one file's write.
    private void OnWatchedMutated(string? name)
    {
        if (!DesktopWallpaperProvider.IsServedFile(name)) return;
        // FSW handlers run on threadpool threads and Dispose does not wait for
        // them, so a late event can race the timer's disposal at shutdown.
        try { _debounce?.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        base.Dispose();
    }
}
