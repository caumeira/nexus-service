using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Migration;

/// <summary>Translated Y70 layout plus a report of what carried over.</summary>
internal sealed class Nexus2Y70LayoutResult
{
    public PanelLayoutDto? Layout { get; set; }
    public int Pages { get; set; }
    public int Widgets { get; set; }
    public int MappedWidgets { get; set; }
    public List<string> DroppedTypes { get; } = new();
}

/// <summary>Translated Y70 theme -> panel appearance (accent + background).</summary>
internal sealed class Nexus2AppearanceResult
{
    public bool Available { get; set; }
    public string? AccentHex { get; set; }
    public string BackgroundMode { get; set; } = "solid";
    public string? BackgroundEffect { get; set; }
    public double? BackgroundOpacity { get; set; }
}

/// <summary>Translated Q60 face: the active page's widget plus every other
/// page's translated config stashed by widget type (first-seen wins).</summary>
internal sealed class Nexus2Q60FaceResult
{
    public bool Available { get; set; }
    public string? ActiveWidgetType { get; set; }
    public Dictionary<string, JsonElement>? ActiveConfig { get; set; }
    public string? AccentHex { get; set; }
    public Dictionary<string, Dictionary<string, JsonElement>> StashedConfigs { get; } = new();
}

/// <summary>Q60 custom wallpaper resolved against q60\web\user-media.</summary>
internal sealed class Nexus2WallpaperResult
{
    public bool Available { get; set; }
    public string? AbsolutePath { get; set; }
    public string? FileName { get; set; }
}

/// <summary>Y70 gallery widgets' referenced files, deduplicated by path.</summary>
internal sealed class Nexus2GallerySourcesResult
{
    public bool Available { get; set; }
    public List<string> ExistingPaths { get; } = new();
    public int Missing { get; set; }
}
