using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// One imported panel-background asset. Stored under
/// panel-backgrounds/&lt;deviceId&gt;/&lt;id&gt;/ as media.mp4 or media.jpg
/// (already cropped and scaled to the device's native resolution), plus
/// thumb.jpg and meta.json. Source file is not retained.
/// </summary>
public sealed class PanelBgItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // "static" | "animated"
    public int Width { get; set; }
    public int Height { get; set; }
    public long ImportedAtUnixMs { get; set; }
    public double DurationSec { get; set; }
}

public sealed class PanelBgListResponse
{
    public List<PanelBgItem> Items { get; set; } = new();
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgImportResponse
{
    public PanelBgItem? Item { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class PanelBgStageResponse
{
    public string? StageId { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}
