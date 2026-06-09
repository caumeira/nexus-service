using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Generic acknowledgement payload for fire-and-forget host actions.
/// AOT-safe replacement for the anonymous-type
/// <c>new { ok, message }</c> idiom which silently fails under the
/// source-gen JSON context.
/// </summary>
public sealed class AppActionAckDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("applied")] public string? Applied { get; set; }
}

/// <summary>Combined cooling read for a widget: fan channels + temperature sources.</summary>
public sealed class AppCoolingStateDto
{
    [JsonPropertyName("channels")] public List<FanChannel> Channels { get; set; } = new();
    [JsonPropertyName("sources")] public List<TemperatureSource> Sources { get; set; } = new();
}

/// <summary>One row in the screentime payload's <c>history</c> array.</summary>
public sealed class ScreentimeHistoryEntryDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("formatted")] public string Formatted { get; set; } = "";
    [JsonPropertyName("pctOfMax")] public int PctOfMax { get; set; }
}

/// <summary>The active app session block.</summary>
public sealed class ScreentimeFocusDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("formatted")] public string Formatted { get; set; } = "";
}

/// <summary>Top-level payload returned by the <c>screentime.today</c>
/// host action.</summary>
public sealed class ScreentimeTodayDto
{
    [JsonPropertyName("focus")] public ScreentimeFocusDto? Focus { get; set; }
    [JsonPropertyName("history")] public List<ScreentimeHistoryEntryDto> History { get; set; } = new();
    [JsonPropertyName("totalMs")] public long TotalMs { get; set; }
    [JsonPropertyName("totalFormatted")] public string TotalFormatted { get; set; } = "";
    [JsonPropertyName("maxMs")] public long MaxMs { get; set; }
    [JsonPropertyName("hasData")] public bool HasData { get; set; }
}
