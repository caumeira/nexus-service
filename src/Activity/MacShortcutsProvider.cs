using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Real macOS shortcuts provider.
/// - GetAll() enumerates /Applications, /System/Applications, and
///   ~/Applications for .app bundles
/// - GetIcon() renders the bundle icon through the shared MacAppIconExtractor
///   (NSWorkspace, so Assets.car-only bundles resolve too)
/// - Launch() shells out to `open -a`
/// </summary>
public sealed class MacShortcutsProvider : IShortcutsProvider
{
    private const int ShortcutIconSizePts = 128;
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);
    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();
    private readonly MacAppIconExtractor _iconExtractor;

    public MacShortcutsProvider(MacAppIconExtractor iconExtractor)
    {
        _iconExtractor = iconExtractor;
    }

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

        // A null (extractor timeout) is not cached, so a transient stall does
        // not pin an empty icon for the whole TTL. The smaller proposed size
        // keeps the TTL cache and the physical deck's per-key downscale at
        // list-icon weight rather than full app-icon reps.
        var extracted = _iconExtractor.ExtractPng(shortcut.Path, ShortcutIconSizePts);
        if (extracted is { Length: > 0 })
        {
            lock (_iconLock)
            { _iconCache[targetId] = (extracted, DateTime.UtcNow + IconCacheTtl); }
            return extracted;
        }
        return Array.Empty<byte>();
    }

    // MacScreenTimeProvider reports LSDisplayName, which resolves to
    // CFBundleDisplayName ?? CFBundleName ?? the .app file name - "Visual
    // Studio Code.app" reports "Code", so the file name alone is wrong.
    public string ResolveProcessName(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null) return "";
        return GetDisplayName(shortcut.Path) ?? shortcut.Name;
    }

    private static string? GetDisplayName(string appPath)
    {
        return ReadPlistString(appPath, "CFBundleDisplayName")
            ?? ReadPlistString(appPath, "CFBundleName");
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

    private static string? GetBundleId(string appPath) =>
        ReadPlistString(appPath, "CFBundleIdentifier");

    private static string? ReadPlistString(string appPath, string key)
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
            psi.ArgumentList.Add(key);
            psi.ArgumentList.Add("raw");
            psi.ArgumentList.Add(plistPath);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            // plutil prints a diagnostic to stdout for a key the plist lacks.
            if (output.Length == 0 || output.Contains("does not exist", StringComparison.Ordinal))
            {
                return null;
            }
            return output;
        }
        catch
        {
            return null;
        }
    }
}
