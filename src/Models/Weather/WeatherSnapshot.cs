using System.Collections.Generic;

namespace Nexus.Service.Models.Weather;

/// <summary>
/// Current weather payload for the panel weather widget.
/// Always returned (even on failure) - all fields nullable / empty-safe so the
/// widget can render a "no data" state without special-casing the response.
/// </summary>
public sealed class WeatherSnapshot
{
    public double? TemperatureC { get; set; }
    public double? TemperatureF { get; set; }
    /// <summary>Open-Meteo WMO weather code (0-99). 0 clear, 1-3 cloudy,
    /// 45/48 fog, 51+ drizzle/rain, 71+ snow, 95+ thunder. -1 when unknown.</summary>
    public int WeatherCode { get; set; } = -1;
    /// <summary>Short human-readable condition label ("Clear", "Cloudy", ...).</summary>
    public string Condition { get; set; } = "";
    public double? HumidityPct { get; set; }
    public double? WindKph { get; set; }
    /// <summary>Best-effort location label ("San Francisco, US"). May be empty
    /// on stub or when geolocation fails.</summary>
    public string LocationLabel { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2 country code from IP geolocation. Used by
    /// the widget to auto-pick C vs F. Empty when unknown.</summary>
    public string CountryCode { get; set; } = "";
    /// <summary>ISO-8601 UTC timestamp of when this snapshot was produced. Empty
    /// string means we have no data at all (stub or complete failure).</summary>
    public string AsOf { get; set; } = "";
    /// <summary>Hourly forecast rows in local weather-provider times.</summary>
    public List<WeatherHourlyForecast> Hourly { get; set; } = new();
    /// <summary>Daily forecast rows in local weather-provider dates.</summary>
    public List<WeatherDailyForecast> Daily { get; set; } = new();

    public static WeatherSnapshot Empty => new();
}

public sealed class WeatherHourlyForecast
{
    public string Time { get; set; } = "";
    public int WeatherCode { get; set; } = -1;
    public double? TemperatureC { get; set; }
    public double? TemperatureF { get; set; }
}

public sealed class WeatherDailyForecast
{
    public string Date { get; set; } = "";
    public int WeatherCode { get; set; } = -1;
    public double? TemperatureMinC { get; set; }
    public double? TemperatureMaxC { get; set; }
    public double? TemperatureMinF { get; set; }
    public double? TemperatureMaxF { get; set; }
}
