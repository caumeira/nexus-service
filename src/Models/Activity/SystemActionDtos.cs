using System.Collections.Generic;
using Nexus.Service.Models.Peripherals.Keeb;

namespace Nexus.Service.Models.Activity;

/// <summary>
/// A keyboard injection request. Either a single chord (Key + modifier flags —
/// the common deck-button case) OR an explicit ordered <see cref="Strokes"/>
/// list. When Strokes is non-empty it wins over the single-chord form.
/// </summary>
public sealed class SendKeysBody
{
    public string Key { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public List<MacroStroke> Strokes { get; set; } = new();
}

/// <summary>Type/paste text. <see cref="Paste"/> uses the clipboard-then-paste path (Unicode-reliable).</summary>
public sealed class SendTextBody
{
    public string Text { get; set; } = "";
    public bool Paste { get; set; } = true;
}

/// <summary>Open a local file or folder with the OS default handler.</summary>
public sealed class OpenPathBody
{
    public string Path { get; set; } = "";
}

/// <summary>OS accent colour as #RRGGBB, or empty when unavailable (e.g. served
/// only on Linux, where the dashboard browser has no native accent push).</summary>
public sealed class SystemAccentResponse
{
    public string Accent { get; set; } = "";
}
