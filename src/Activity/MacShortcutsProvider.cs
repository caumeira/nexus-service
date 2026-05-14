using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Qos.Service.Models.Activity;

namespace Qos.Service.Activity;

/// <summary>
/// Real macOS shortcuts provider.
/// - GetAll() enumerates /Applications, /System/Applications, and
///   ~/Applications for .app bundles
/// - GetIcon() extracts the app icon from the bundle's Resources/*.icns, converts to PNG via sips
/// - Launch() shells out to `open -a`
/// </summary>
public sealed class MacShortcutsProvider : IShortcutsProvider
{
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);
    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();

    public IReadOnlyList<Shortcut> GetAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Array.Empty<Shortcut>();
        }

        var apps = new List<Shortcut>();
        var searchDirs = new[]
        {
            "/Applications",
            "/System/Applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var appPath in Directory.GetDirectories(dir, "*.app", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(appPath);
                    var bundleId = GetBundleId(appPath);
                    apps.Add(new Shortcut
                    {
                        Id = bundleId ?? name,
                        Name = name,
                        Path = appPath,
                    });
                }
            }
            catch { /* access denied, etc. */ }
        }

        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Shortcut? GetById(string targetId)
    {
        return GetAll().FirstOrDefault(s =>
            string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, targetId, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] GetIcon(string targetId)
    {
        // Check cache first
        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(targetId, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                return cached.Data;
            }
        }

        var shortcut = GetById(targetId);
        if (shortcut is null)
        {
            return Array.Empty<byte>();
        }

        try
        {
            // Find the .icns in the bundle
            var resourcesDir = Path.Combine(shortcut.Path, "Contents", "Resources");
            if (!Directory.Exists(resourcesDir))
            {
                return Array.Empty<byte>();
            }

            var icnsFile = Directory.GetFiles(resourcesDir, "*.icns").FirstOrDefault();
            if (icnsFile is null)
            {
                return Array.Empty<byte>();
            }

            // Convert .icns → .png via sips (ships with macOS, no dep)
            var tmpPng = Path.GetTempFileName() + ".png";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/sips",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add("format");
                psi.ArgumentList.Add("png");
                psi.ArgumentList.Add(icnsFile);
                psi.ArgumentList.Add("--out");
                psi.ArgumentList.Add(tmpPng);
                psi.ArgumentList.Add("--resampleWidth");
                psi.ArgumentList.Add("128");

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                if (File.Exists(tmpPng))
                {
                    var bytes = File.ReadAllBytes(tmpPng);
                    lock (_iconLock)
                    { _iconCache[targetId] = (bytes, DateTime.UtcNow + IconCacheTtl); }
                    return bytes;
                }
            }
            finally
            {
                try
                { File.Delete(tmpPng); }
                catch { }
            }
        }
        catch { }

        return Array.Empty<byte>();
    }

    public bool Launch(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(shortcut.Path);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetBundleId(string appPath)
    {
        try
        {
            var plistPath = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plistPath))
            {
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/plutil",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-extract");
            psi.ArgumentList.Add("CFBundleIdentifier");
            psi.ArgumentList.Add("raw");
            psi.ArgumentList.Add(plistPath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
