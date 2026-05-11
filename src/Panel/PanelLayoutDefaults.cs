using System;
using System.Collections.Generic;
using Qos.Service.Models.Panel;

namespace Qos.Service.Panel;

/// <summary>
/// Starter layout for a freshly registered panel device. Minimal on
/// purpose: clock at the top, monitoring under it. The runtime shows
/// this until the user saves their first edit, at which point the
/// edit is persisted on the device record and the default is no
/// longer consulted.
/// </summary>
public static class PanelLayoutDefaults
{
    public static PanelLayoutDto Default()
    {
        return new PanelLayoutDto
        {
            LayoutSchemaVersion = 2,
            Surface = "y70",
            Pages = new List<PanelPageDto>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Widgets = new List<PanelWidgetDto>
                    {
                        Widget("clock",      "4x2", 0, 0),
                        Widget("monitoring", "4x4", 0, 2),
                    },
                },
            },
        };
    }

    private static PanelWidgetDto Widget(string type, string size, int col, int row) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Size = size,
        Col = col,
        Row = row,
    };
}
