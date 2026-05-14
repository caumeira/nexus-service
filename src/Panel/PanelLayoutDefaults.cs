using System;
using System.Collections.Generic;
using System.Linq;
using Qos.Service.Defaults;
using Qos.Service.Models.Panel;

namespace Qos.Service.Panel;

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
        var src = surface switch
        {
            "desktop" => InstallDefaults.Panel.Layouts.Desktop,
            "phone" => InstallDefaults.Panel.Layouts.Phone,
            "q60" => InstallDefaults.Panel.Layouts.Q60,
            _ => InstallDefaults.Panel.Layouts.Y70,
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
                    }).ToList(),
                },
            },
        };
    }
}
