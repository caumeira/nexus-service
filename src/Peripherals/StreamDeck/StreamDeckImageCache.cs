using System;
using System.IO;
using System.Security.Cryptography;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Content-addressed disk cache for uploaded key-image bytes:
/// <c>%ProgramData%\Nexus\streamdeck\&lt;serial&gt;\&lt;contentHash&gt;.bin</c>.
/// Lets a reconnect or service restart re-push a deck's keys without the web
/// editor re-uploading anything.
/// </summary>
public sealed class StreamDeckImageCache
{
    private readonly string _root;

    public StreamDeckImageCache() : this(DefaultRoot())
    {
    }

    /// <summary>Test seam: points the cache at a tmp directory instead of real ProgramData.</summary>
    public StreamDeckImageCache(string root)
    {
        _root = root;
    }

    private static string DefaultRoot()
    {
        // Mirrors JsonConfigStore.ResolveSettingsPath: CommonApplicationData is
        // %ProgramData% only on Windows; on macOS it maps to the unwritable
        // /usr/share, so the cache must live beside settings.json instead.
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus", "streamdeck");
        }
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "streamdeck");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus", "streamdeck");
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Whitelists a device serial before it becomes a directory component,
    /// matching every observed shape (real Elgato serials, the sd-&lt;hex&gt;
    /// HID-path fallback, the sim-0001 simulator id) while rejecting path
    /// traversal segments like ".." or "/".
    /// </summary>
    public static bool IsValidSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 64)
        {
            return false;
        }
        foreach (var ch in serial)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Writes the bytes if not already cached (content-addressed, so a re-upload of the same image is a no-op write). No-ops on an invalid serial.</summary>
    public void Store(string serial, string hash, byte[] bytes)
    {
        if (!IsValidSerial(serial))
        {
            return;
        }
        var path = PathFor(serial, hash);
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Returns the cached bytes, or null when never stored, the serial is invalid, or the cache was wiped.</summary>
    public byte[]? Load(string serial, string hash)
    {
        if (!IsValidSerial(serial))
        {
            return null;
        }
        try
        {
            var path = PathFor(serial, hash);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes one cached image. Used to evict a hash no longer referenced by any slot after a replace.</summary>
    public void Evict(string serial, string hash)
    {
        if (!IsValidSerial(serial))
        {
            return;
        }
        try
        {
            File.Delete(PathFor(serial, hash));
        }
        catch
        {
            /* best effort */
        }
    }

    private string PathFor(string serial, string hash) => Path.Combine(_root, serial, hash + ".bin");
}
