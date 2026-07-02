using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Built-in preset cover thumbnails (default_01..06), embedded in the assembly and
/// exposed as base64 data URLs. The panel is FLAG_SECURE and cannot produce a live thumbnail,
/// so these ship with the app; they are the Kanali preset covers, downscaled.</summary>
public static class TryxPresetThumbs
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>Data URL for a preset id (e.g. "default_03"), or null if none is bundled.</summary>
    public static string? DataUrl(string presetId)
        => Cache.GetOrAdd(presetId, Load);

    private static string? Load(string presetId)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"tryx-preset-{presetId}.jpg");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
