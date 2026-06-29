using System;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Persisted metadata for one LCD media item. Written to meta.json alongside
/// the JPEG frame files under the library root.
/// </summary>
public sealed class LcdMediaItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"static" | "animated"</summary>
    public string Type { get; set; } = "";
    public int Frames { get; set; }
    /// <summary>Per-frame display duration in milliseconds, parsed from GIF GCE blocks at import time. Empty for static images.</summary>
    public int[] Delays { get; set; } = Array.Empty<int>();
    public long ImportedAtUnixMs { get; set; }
}

/// <summary>In-memory LCD image data ready for streaming. Not persisted.</summary>
public sealed class LcdImageData
{
    public string Id { get; set; } = "";
    public LcdFrame[] Frames { get; set; } = Array.Empty<LcdFrame>();
}

/// <summary>One 480x480 JPEG frame with its display duration.</summary>
public sealed class LcdFrame
{
    public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
    /// <summary>Display duration in milliseconds. 0 for static frames; worker applies 10ms minimum.</summary>
    public int DelayMs { get; set; }
}
