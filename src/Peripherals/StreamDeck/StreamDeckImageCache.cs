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
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonAppData, "Nexus", "streamdeck");
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Writes the bytes if not already cached (content-addressed, so a re-upload of the same image is a no-op write).</summary>
    public void Store(string serial, string hash, byte[] bytes)
    {
        var path = PathFor(serial, hash);
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Returns the cached bytes, or null when never stored (or the cache was wiped).</summary>
    public byte[]? Load(string serial, string hash)
    {
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

    private string PathFor(string serial, string hash) => Path.Combine(_root, serial, hash + ".bin");
}
