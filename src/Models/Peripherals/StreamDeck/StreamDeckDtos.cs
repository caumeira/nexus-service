using System.Collections.Generic;

namespace Nexus.Service.Models.Peripherals.StreamDeck;

/// <summary>One detected (real or simulated) Stream Deck, for GET /streamdeck/decks.</summary>
public sealed class StreamDeckSummaryDto
{
    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public int Rows { get; set; }
    public int Columns { get; set; }
    public bool Connected { get; set; }
    public bool Verified { get; set; }
    public bool Simulated { get; set; }
    public string FirmwareVersion { get; set; } = "";
}

public sealed class GetStreamDecksResponse
{
    public List<StreamDeckSummaryDto> Decks { get; set; } = new();
}

public sealed class SetStreamDeckBrightnessBody
{
    public int Percent { get; set; }
}

public sealed class StreamDeckSimPressBody
{
    public int KeyIndex { get; set; }
    public bool Pressed { get; set; }
}
