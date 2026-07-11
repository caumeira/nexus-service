using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Deck;

/// <summary>
/// C# mirror of nexus-web's <c>panel/widgets/deck/types.ts</c> - the binding
/// model both the virtual touch deck widget and physical Stream Deck configs
/// share. Keep field-for-field in sync with that file. The wire shape is
/// byte-for-byte the TS union (nexus-web's api/streamdeck.ts round-trips the
/// native <c>DeckConfig</c> with no adapter), even though the reused JSON key
/// "action" (system/nexus/power) needs three distinct C# properties -
/// <see cref="SystemAction"/>, <see cref="NexusAction"/>,
/// <see cref="PowerAction"/> - handled by <see cref="DeckActionConverter"/>.
/// </summary>
[JsonConverter(typeof(DeckActionConverter))]
public sealed class DeckAction
{
    /// <summary>
    /// launchApp | openFile | openFolder | openUrl | system | hotkey | text |
    /// power | audioOutput | audioInput | nexus | sequence | toggle | page |
    /// pageIndicator | deckBrightness | deckSleep | hotkeySwitch.
    /// </summary>
    public string Type { get; set; } = "";

    public string? AppId { get; set; }
    public string? Path { get; set; }
    public string? Url { get; set; }

    public DeckSystemAction? SystemAction { get; set; }
    public DeckNexusAction? NexusAction { get; set; }

    public string? Keys { get; set; }
    public string? Text { get; set; }
    public bool? Paste { get; set; }

    /// <summary>lock | sleep | shutdown | restart | logout.</summary>
    public string? PowerAction { get; set; }

    /// <summary>audioOutput / audioInput device id.</summary>
    public string? DeviceId { get; set; }

    public List<DeckSequenceStep>? Steps { get; set; }

    /// <summary>Branch taken when the toggle resolves on.</summary>
    public DeckAction? On { get; set; }
    /// <summary>Branch taken when the toggle resolves off.</summary>
    public DeckAction? Off { get; set; }
    public DeckToggleState? State { get; set; }

    /// <summary>page: next | prev | goto. deckBrightness: set | up | down.</summary>
    public string? Op { get; set; }
    /// <summary>page goto: 0-based target page index.</summary>
    public int? Target { get; set; }
    /// <summary>deckBrightness set: target percent, 0-100.</summary>
    public int? Value { get; set; }
    /// <summary>deckBrightness up/down: step percent; unset falls back to DeckActionExecutor.DeckBrightnessStep.</summary>
    public int? Step { get; set; }
    /// <summary>hotkeySwitch: first combo, same format as Keys.</summary>
    public string? KeysA { get; set; }
    /// <summary>hotkeySwitch: second combo, alternated with KeysA on each press.</summary>
    public string? KeysB { get; set; }
}

public sealed class DeckSystemAction
{
    /// <summary>
    /// volumeUp | volumeDown | volumeSet | muteToggle | mediaPlayPause |
    /// mediaNext | mediaPrev | brightnessUp | brightnessDown | brightnessSet |
    /// openSettings.
    /// </summary>
    public string Op { get; set; } = "";

    /// <summary>volumeSet (0..1), brightnessSet (0..100).</summary>
    public double? Value { get; set; }
    /// <summary>Up/down increment as a percent (0..100) of the target's own range, for both volume* and brightness*.</summary>
    public double? Step { get; set; }
    /// <summary>brightness* target display id.</summary>
    public string? DisplayId { get; set; }
    /// <summary>media* source id; omitted picks the active session.</summary>
    public string? Source { get; set; }
}

public sealed class DeckNexusAction
{
    /// <summary>
    /// rgbEffect | rgbScene | lightingBrightness | lightingPower | fanProfile |
    /// fanSpeed | y70Power | y70Brightness | y70Rotation.
    /// </summary>
    public string Op { get; set; } = "";

    /// <summary>rgbEffect.</summary>
    public string? Effect { get; set; }
    /// <summary>rgbScene.</summary>
    public string? ProfileId { get; set; }
    /// <summary>fanProfile preset name.</summary>
    public string? Profile { get; set; }
    /// <summary>lightingPower.</summary>
    public string? DeviceId { get; set; }
    /// <summary>fanSpeed.</summary>
    public string? FanId { get; set; }
    /// <summary>lightingBrightness (0..1), fanSpeed (0..100), y70Brightness (0..100).</summary>
    public double? Value { get; set; }
    /// <summary>lightingPower, y70Power.</summary>
    public bool? On { get; set; }
    /// <summary>y70Rotation.</summary>
    public string? Orientation { get; set; }
}

public sealed class DeckSequenceStep
{
    public DeckAction Action { get; set; } = new();
    /// <summary>Milliseconds this step's action is held before release.</summary>
    public int? PressMs { get; set; }
    /// <summary>Milliseconds idled before the next step.</summary>
    public int? GapAfterMs { get; set; }
}

public sealed class DeckToggleState
{
    /// <summary>mute | lightingPower | internal.</summary>
    public string Kind { get; set; } = "";
    /// <summary>lightingPower.</summary>
    public string? DeviceId { get; set; }
}

public sealed class DeckIcon
{
    /// <summary>lucide | emoji | app.</summary>
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class DeckSlot
{
    /// <summary>Unset falls back to an auto icon by action category.</summary>
    public DeckIcon? Icon { get; set; }
    public string? Label { get; set; }
    /// <summary>Unset falls back to an auto color by action category.</summary>
    public string? Color { get; set; }
    /// <summary>Styling for Label. Unset falls back to nexus-web's deckTitleStyle.ts defaults.</summary>
    public DeckTitleStyle? Title { get; set; }
    /// <summary>A slot is an action, a folder, or empty - never both.</summary>
    public DeckAction? Action { get; set; }
    public DeckFolder? Folder { get; set; }
}

public sealed class DeckTitleStyle
{
    public bool? Show { get; set; }
    /// <summary>top | middle | bottom.</summary>
    public string? Align { get; set; }
    public string? Font { get; set; }
    public int? Size { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public string? Color { get; set; }
}

public sealed class DeckFolder
{
    public List<DeckSlot> Slots { get; set; } = new();
}

/// <summary>One page's grid. Folders still nest within a page via DeckSlot.Folder.</summary>
public sealed class DeckPage
{
    public List<DeckSlot> Slots { get; set; } = new();
}

/// <summary>
/// An ordered list of pages, the axis a deck's page-navigation keys move
/// between. See <see cref="DeckConfigConverter"/> for the wire shape and its
/// legacy-slots back-compat.
/// </summary>
[JsonConverter(typeof(DeckConfigConverter))]
public sealed class DeckConfig
{
    public List<DeckPage> Pages { get; set; } = new();
}
