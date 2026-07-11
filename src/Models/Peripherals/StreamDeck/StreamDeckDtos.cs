using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nexus.Service.Deck;

namespace Nexus.Service.Models.Peripherals.StreamDeck;

/// <summary>One detected (real or simulated) Stream Deck, for GET /streamdeck/decks.</summary>
public sealed class StreamDeckSummaryDto
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Persisted display name; falls back to the model name when unset.</summary>
    public string Name { get; set; } = "";
    public bool Connected { get; set; }
    public bool Verified { get; set; }
    public int Rows { get; set; }
    /// <summary>Wire name "cols" per the streamdeck-support.md contract, not "columns".</summary>
    [JsonPropertyName("cols")]
    public int Columns { get; set; }
    public int KeyCount { get; set; }
    public int KeyPixels { get; set; }
    /// <summary>"bmp" | "jpeg".</summary>
    public string Format { get; set; } = "";
    /// <summary>"none" | "flipBoth" | "mirrorXRot90" - see StreamDeckModel.Transform.</summary>
    public string Transform { get; set; } = "";
    /// <summary>Persisted value, 0-100.</summary>
    public int Brightness { get; set; }
    /// <summary>User rotation, in quarter-turn degree steps.</summary>
    public int Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; a non-positive value disables sleep-after.</summary>
    public int SleepAfterSeconds { get; set; }
    public string FirmwareVersion { get; set; } = "";
    /// <summary>"elgato-software-running" when Elgato's own app is contending for the deck; null otherwise.</summary>
    public string? Warning { get; set; }
    /// <summary>ConflictAppCatalog id to pass to POST /conflicts/kill when Warning is set; null otherwise.</summary>
    public string? ConflictAppId { get; set; }
}

public sealed class GetStreamDecksResponse
{
    public List<StreamDeckSummaryDto> Decks { get; set; } = new();
}

/// <summary>POST /streamdeck/decks/{serial} - rename and/or set brightness/orientation/sleep-after. Any field may be omitted.</summary>
public sealed class UpdateStreamDeckBody
{
    public string? Name { get; set; }
    public int? Brightness { get; set; }
    /// <summary>Degrees; clamped to the nearest quarter-turn.</summary>
    public int? Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks; clamped to a non-negative value.</summary>
    public int? SleepAfterSeconds { get; set; }
}

/// <summary>Shared envelope for GET/PUT /streamdeck/decks/{serial}/config.</summary>
public sealed class StreamDeckConfigEnvelope
{
    public DeckConfig Config { get; set; } = new();
}

/// <summary>PUT /streamdeck/decks/{serial}/images/{slotPath}/{state} response.</summary>
public sealed class StreamDeckImageUploadResponse
{
    public string Hash { get; set; } = "";
}

/// <summary>Multiplex frame for the "streamdeck" topic.</summary>
public sealed class StreamDeckChangedFrame
{
    public long Revision { get; set; }
    /// <summary>"decks" | "config" | "nav" | "press".</summary>
    public string Kind { get; set; } = "";
    public string? Serial { get; set; }
    /// <summary>Current folder path, for "nav" and "press".</summary>
    public List<int>? FolderPath { get; set; }
    /// <summary>Logical slot index within the current folder view, for "press".</summary>
    public int? KeyIndex { get; set; }
}

public sealed class StreamDeckSimPressBody
{
    public int KeyIndex { get; set; }
    public bool Pressed { get; set; }
}
