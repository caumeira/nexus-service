using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Defaults;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// Starter layout for a freshly registered panel device. Per-surface
/// definitions live in data/install-defaults.json under panel.layouts;
/// the runtime shows them until the user saves their first edit, at
/// which point the edit is persisted on the device record and the
/// default is no longer consulted.
/// </summary>
public static class PanelLayoutDefaults
{
    public static PanelLayoutDto Default() => ForSurface("y70");

    public static PanelLayoutDto ForSurface(string surface)
    {
        // Layouts is nullable on the shared PanelSettings POCO (live profiles
        // leave it null); install-defaults always populates it.
        var layouts = InstallDefaults.Panel.Layouts ?? new PanelLayoutsDefaults();
        var src = surface switch
        {
            "desktop" => layouts.Desktop,
            "phone" => layouts.Phone,
            "q60" => layouts.Q60,
            _ => layouts.Y70,
        };
        return new PanelLayoutDto
        {
            LayoutSchemaVersion = src.LayoutSchemaVersion,
            // Caller's surface argument wins regardless of what the JSON
            // entry's surface field says — guards against a hand-edit mistake
            // that would otherwise tag a `phone` layout as `y70`.
            Surface = surface,
            Pages = new List<PanelPageDto>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Widgets = src.Widgets.Select(w => new PanelWidgetDto
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = w.Type,
                        Size = w.Size,
                        Col = w.Col,
                        Row = w.Row,
                        // Carry the seed's Config through so install-defaults
                        // can pre-populate per-widget settings (e.g. monitoring
                        // sensors, clock design). Null means "use the widget's
                        // own component-default behavior".
                        Config = w.Config,
                    }).ToList(),
                },
            },
        };
    }
}
