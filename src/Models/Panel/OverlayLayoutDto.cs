using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// Floating desktop widget. Positioned by cell coordinates on a sparse grid;
/// each monitor has its own (col, row) space sized from monitor workArea
/// divided by the panel cell size. See plans/desktop-widgets-v1.md.
/// </summary>
public sealed class OverlayWidgetDto
{
    public string Id { get; set; } = "";

    /// <summary>Widget kind key ("clock", "performance", ...). Mirrors PanelWidgetDto.Type.</summary>
    public string Type { get; set; } = "";

    /// <summary>"1x1" | "2x2" | "2x4" | "4x2" | "4x4". Mirrors PanelWidgetDto.Size.</summary>
    public string Size { get; set; } = "2x2";

    /// <summary>0-based monitor index in EnumDisplayMonitors order.</summary>
    public int Monitor { get; set; }

    /// <summary>Top-left cell column on the target monitor. Fractional values
    /// allowed in 0.25 increments so users can position at sub-cell precision
    /// (drag-aligned grid is 4x finer than the widget sizing grid).</summary>
    public double Col { get; set; }

    /// <summary>Top-left cell row on the target monitor. Same fractional rules
    /// as <see cref="Col"/>.</summary>
    public double Row { get; set; }

    /// <summary>When true the SPA blocks drag/edit/resize of this widget until
    /// the user unlocks it. Inner widget controls stay live.</summary>
    public bool Locked { get; set; }

    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}

/// <summary>
/// POST /overlay/widgets body. Server fills any omitted field: Size from registry
/// default, Monitor=0, (Col,Row) = first free contiguous block scanning row-major
/// on that monitor, Config empty.
/// </summary>
public sealed class OverlayWidgetCreateBody
{
    public string Type { get; set; } = "";
    public string? Size { get; set; }
    public int? Monitor { get; set; }
    public double? Col { get; set; }
    public double? Row { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}

/// <summary>
/// PATCH /overlay/widgets/{id} body. Every field optional; only present fields
/// are applied. Same partial-update pattern as UiSettingsPatch.
/// </summary>
public sealed class OverlayWidgetPatch
{
    public string? Size { get; set; }
    public int? Monitor { get; set; }
    public double? Col { get; set; }
    public double? Row { get; set; }
    public bool? Locked { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}

/// <summary>
/// POST /overlay/widgets/lock body. Sets <see cref="OverlayWidgetDto.Locked"/>
/// on every widget in one shot so "Lock all" / "Unlock all" land atomically
/// with a single prefs broadcast.
/// </summary>
public sealed class OverlayLockAllBody
{
    public bool Locked { get; set; }
}
