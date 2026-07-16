using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

/// <summary>
/// Resolves the console user's current desktop wallpaper file for panel
/// backgrounds. The service runs as LocalSystem, whose own profile has no
/// wallpaper - resolution goes through the active console user's profile,
/// never HKCU (the LocalSystem HKCU is the SYSTEM hive).
/// </summary>
public static class DesktopWallpaperProvider
{
    public static (string Path, DateTime MTimeUtc)? TryResolve(int width, int height)
    {
        var themes = ResolveThemesDir();
        if (themes is null) return null;
        // The shell writes a per-monitor crop for each attached resolution:
        // CachedFiles\CachedImage_{w}_{h}_POS{n}.jpg. An exact resolution
        // match is the panel monitor's own crop; otherwise serve the full
        // transcoded image and let the client cover-fit it (the same crop the
        // shell's default Fill style produces).
        if (width > 0 && height > 0)
        {
            var cached = Path.Combine(themes, "CachedFiles");
            if (Directory.Exists(cached))
            {
                var match = Directory.EnumerateFiles(cached, $"CachedImage_{width}_{height}_*.jpg")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (match is not null) return (match, File.GetLastWriteTimeUtc(match));
            }
        }
        var transcoded = Path.Combine(themes, "TranscodedWallpaper");
        if (File.Exists(transcoded)) return (transcoded, File.GetLastWriteTimeUtc(transcoded));
        return null;
    }

    internal static string? ResolveThemesDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var user = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(user)) return null;
        // Profile-on-C assumption: matches every supported install; a relocated
        // ProfilesDirectory resolves to null and the endpoint 404s (panel keeps
        // its theme backdrop).
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
/// user's wallpaper changes (the shell rewrites TranscodedWallpaper and the
/// CachedFiles crops on every change). The themes dir only resolves once a
/// user is logged on, so arming retries on a slow cadence - same deferral the
/// helper bootstrapper needs at boot.
/// </summary>
public sealed class DesktopWallpaperWatcher : BackgroundService, IDisposable
{
    private readonly MultiplexHub _hub;
    private FileSystemWatcher? _watcher;
    private long _lastBroadcastTick;

    public DesktopWallpaperWatcher(MultiplexHub hub)
    {
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_watcher is null)
            {
                var themes = DesktopWallpaperProvider.ResolveThemesDir();
                if (themes is not null)
                {
                    try
                    {
                        _watcher = Arm(themes);
                        ServiceLog.Info($"[wallpaper-watch] armed on {themes}");
                    }
                    catch (Exception ex)
                    {
                        ServiceLog.Warn($"[wallpaper-watch] arm failed: {ex.Message}");
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private FileSystemWatcher Arm(string themes)
    {
        var watcher = new FileSystemWatcher(themes)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        watcher.Changed += (_, _) => OnThemesMutated();
        watcher.Created += (_, _) => OnThemesMutated();
        watcher.Renamed += (_, _) => OnThemesMutated();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    // One wallpaper change mutates several files; collapse the burst to one
    // broadcast per 2s window. Subscribers just refetch, so a dropped trailing
    // event only delays the refresh to the next change.
    private void OnThemesMutated()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastBroadcastTick) < 2000) return;
        Interlocked.Exchange(ref _lastBroadcastTick, now);
        PanelTopics.BroadcastDesktopWallpaper(_hub);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
