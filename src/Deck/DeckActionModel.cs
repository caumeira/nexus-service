using System.Collections.Generic;

namespace Nexus.Service.Deck;

/// <summary>
/// C# mirror of nexus-web's <c>panel/widgets/deck/types.ts</c> - the binding
/// model both the virtual touch deck widget and physical Stream Deck configs
/// share. Keep field-for-field in sync with that file.
///
/// System.Text.Json's source generator rejects two properties that resolve
/// to the same JSON name, but the TS union reuses the field name "action" for
/// three different variants (system/nexus/power). This mirror instead uses
/// three distinct properties - <see cref="SystemAction"/>, <see cref="NexusAction"/>,
/// <see cref="PowerAction"/> - so a wire payload produced straight from the TS
/// <c>DeckAction</c> shape does not round-trip unmodified; the web-side
/// physical-deck target adapter must map between the two shapes.
/// </summary>
public sealed class DeckAction
{
    /// <summary>
    /// launchApp | openFile | openFolder | openUrl | system | hotkey | text |
    /// power | audioOutput | audioInput | nexus | sequence | toggle.
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
    /// <summary>Up/down increment.</summary>
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
    /// <summary>A slot is an action, a folder, or empty - never both.</summary>
    public DeckAction? Action { get; set; }
    public DeckFolder? Folder { get; set; }
}

public sealed class DeckFolder
{
    public List<DeckSlot> Slots { get; set; } = new();
}

public sealed class DeckConfig
{
    public List<DeckSlot> Slots { get; set; } = new();
}
