using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>One N2 performance/monitoring slot, pre-extracted from either the
/// Y70 (typed sensor.name/sensor.type) or Q60 (raw sensorId string) shape so
/// <see cref="Nexus2ConfigBuilders.Monitoring"/> can treat both uniformly.</summary>
internal sealed record Nexus2SlotInput(string? Device, string? SensorType, string? SensorName, string? RawSensorId, string? Design);

/// <summary>Widget-config builders shared by the Y70 and Q60 translators - each
/// takes already-extracted primitives so the two callers' differing JSON
/// shapes stay local to them.</summary>
internal static class Nexus2ConfigBuilders
{
    /// <summary>mappedDesign is already the resolved Nexus 3 clock design key
    /// (Y70 via MapClockDesign, Q60 via MapQ60ClockDesign) - the two source
    /// vocabularies differ, so callers map before calling this.</summary>
    public static Dictionary<string, JsonElement> Clock(string? mappedDesign, string? timeFormat, bool showSeconds, bool showTimezone, string? timezone)
    {
        var b = new Nexus2ConfigBuilder();
        if (mappedDesign is not null)
        {
            b.String("design", mappedDesign);
        }
        b.String("format", Nexus2WidgetMapping.MapTimeFormat(timeFormat) ?? "auto");
        b.Bool("showSeconds", showSeconds);
        b.Bool("showTimezone", showTimezone);
        if (!string.IsNullOrEmpty(timezone))
        {
            b.String("timezone", timezone);
        }
        return b.Build();
    }

    public static Dictionary<string, JsonElement> Weather(string? units, double? lat, double? lon, string? label)
    {
        var b = new Nexus2ConfigBuilder();
        b.String("unit", Nexus2WidgetMapping.MapWeatherUnit(units) ?? "auto");
        if (lat is not null && lon is not null)
        {
            b.StartObject("location");
            b.Number("lat", lat.Value);
            b.Number("lon", lon.Value);
            b.String("label", label ?? "");
            b.String("cc", "");
            b.EndObject();
        }
        return b.Build();
    }

    /// <summary>Null when there are no slots to translate at all (the caller then
    /// leaves Config unset so the widget renders its own default slots).</summary>
    public static Dictionary<string, JsonElement>? Monitoring(IReadOnlyList<Nexus2SlotInput> slots)
    {
        if (slots.Count == 0)
        {
            return null;
        }
        var count = Nexus2WidgetMapping.ClampSlotCount(slots.Count);
        var b = new Nexus2ConfigBuilder();
        b.Number("slotCount", count);
        for (var i = 0; i < count; i++)
        {
            var slot = slots[i];
            var sensorId = slot.RawSensorId is not null
                ? Nexus2WidgetMapping.ResolveSummarySensorIdFromRawId(slot.Device, slot.RawSensorId)
                : Nexus2WidgetMapping.ResolveSummarySensorId(slot.Device, slot.SensorType, slot.SensorName);
            if (sensorId is not null)
            {
                b.String($"slot{i}_device", "quick");
                b.String($"slot{i}_sensor", sensorId);
            }
            var design = Nexus2WidgetMapping.MapGaugeDesign(slot.Design);
            if (design is not null)
            {
                b.String($"slot{i}_design", design);
            }
        }
        return b.Build();
    }
}
