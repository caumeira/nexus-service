using System;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>One discovered SL-LCD Wireless screen and its fan-position mapping, as tracked by <see cref="Slv3LcdHub"/>.</summary>
public sealed class Slv3LcdScreenInfo
{
    public string Serial { get; set; } = "";
    /// <summary>GroupIndex from GetPosIndex(201); -1 until the query succeeds.</summary>
    public int Position { get; set; } = -1;
}

/// <summary>
/// Persisted metadata for one LCD media item. Written to meta.json alongside
/// the JPEG frame files under the library root.
/// </summary>
public sealed class Slv3LcdMediaItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"image" | "gif" | "video".</summary>
    public string Kind { get; set; } = "";
    public int Frames { get; set; }
    /// <summary>Per-frame display duration in milliseconds. Empty for a single still.</summary>
    public int[] Delays { get; set; } = Array.Empty<int>();
    public long ImportedAtUnixMs { get; set; }
}

/// <summary>In-memory LCD image data ready for streaming. Not persisted.</summary>
public sealed class Slv3LcdImageData
{
    public string Id { get; set; } = "";
    public Slv3LcdFrame[] Frames { get; set; } = Array.Empty<Slv3LcdFrame>();
}

/// <summary>One JPEG frame sized to the panel's fixed resolution, with its display duration.</summary>
public sealed class Slv3LcdFrame
{
    public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
    /// <summary>Display duration in milliseconds. 0 for a static frame; the streaming worker applies a floor.</summary>
    public int DelayMs { get; set; }
}
