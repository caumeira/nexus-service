using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Weather;

namespace Qos.Service.Platform.Weather;

/// <summary>
/// Cross-platform weather provider backed by Open-Meteo (no API key, free,
/// commercial use allowed). Location is resolved via IP geolocation (ipwho.is,
/// no API key, HTTPS, permissive CORS + User-Agent policy) - accurate to the
/// city level, which is enough for a device-panel widget and avoids interactive
/// OS permission prompts inside a kiosk service.
///
/// Both location and weather responses are cached: location for 24 h, weather
/// for 15 min. A single request failure returns the last-known good snapshot
/// if it's still reasonably fresh; total failure returns WeatherSnapshot.Empty.
/// </summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    private static readonly TimeSpan WeatherTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LocationTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleServeTtl = TimeSpan.FromHours(2);

    private readonly IHttpClientFactory _http;

    private IpLocation? _cachedLocation;
    private DateTime _locationFetchedUtc = DateTime.MinValue;

    private WeatherSnapshot? _cachedWeather;
    private DateTime _weatherFetchedUtc = DateTime.MinValue;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public OpenMeteoWeatherProvider(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<WeatherSnapshot> GetCurrentAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;

            // Serve cached snapshot while it's still fresh.
            if (_cachedWeather is not null && (now - _weatherFetchedUtc) < WeatherTtl)
            {
                return _cachedWeather;
            }

            var loc = await GetLocationAsync().ConfigureAwait(false);
            if (loc is null)
            {
                // Fall back to last-known good snapshot if it's still recentish.
                if (_cachedWeather is not null && (now - _weatherFetchedUtc) < StaleServeTtl)
                {
                    return _cachedWeather;
                }
                return WeatherSnapshot.Empty;
            }

            var snapshot = await FetchWeatherAsync(loc).ConfigureAwait(false);
            if (snapshot is not null)
            {
                _cachedWeather = snapshot;
                _weatherFetchedUtc = now;
                return snapshot;
            }

            // Upstream failed; serve stale if available.
            if (_cachedWeather is not null && (now - _weatherFetchedUtc) < StaleServeTtl)
            {
                return _cachedWeather;
            }
            return WeatherSnapshot.Empty;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] failed: {ex.Message}");
            return WeatherSnapshot.Empty;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IpLocation?> GetLocationAsync()
    {
        var now = DateTime.UtcNow;
        if (_cachedLocation is not null && (now - _locationFetchedUtc) < LocationTtl)
        {
            return _cachedLocation;
        }

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var resp = await client.GetAsync("https://ipwho.is/").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[weather] ipwho.is returned {(int)resp.StatusCode}");
                return _cachedLocation;
            }

            var loc = await resp.Content.ReadFromJsonAsync(
                Qos.Service.Serialization.AppJsonContext.Default.IpLocation
            ).ConfigureAwait(false);
            if (loc is null || loc.Latitude is null || loc.Longitude is null)
            {
                Console.Error.WriteLine($"[weather] ipwho.is returned empty lat/lon");
                return _cachedLocation;
            }

            _cachedLocation = loc;
            _locationFetchedUtc = now;
            return loc;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] ipwho.is lookup failed: {ex.Message}");
            return _cachedLocation;
        }
    }

    private async Task<WeatherSnapshot?> FetchWeatherAsync(IpLocation loc)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            var url = $"https://api.open-meteo.com/v1/forecast"
                + $"?latitude={loc.Latitude}&longitude={loc.Longitude}"
                + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m"
                + "&hourly=temperature_2m,weather_code"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                + "&temperature_unit=celsius&wind_speed_unit=kmh&forecast_days=7&timezone=auto";
            var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var payload = await resp.Content.ReadFromJsonAsync(
                Qos.Service.Serialization.AppJsonContext.Default.OpenMeteoResponse
            ).ConfigureAwait(false);
            if (payload?.Current is null)
                return null;

            var c = payload.Current;
            var tempC = c.Temperature2m;
            var tempF = tempC is null ? (double?)null : (tempC.Value * 9.0 / 5.0 + 32.0);

            return new WeatherSnapshot
            {
                TemperatureC = tempC,
                TemperatureF = tempF,
                WeatherCode = c.WeatherCode ?? -1,
                Condition = ConditionFor(c.WeatherCode),
                HumidityPct = c.RelativeHumidity2m,
                WindKph = c.WindSpeed10m,
                LocationLabel = FormatLocation(loc),
                CountryCode = loc.CountryCode ?? "",
                AsOf = DateTime.UtcNow.ToString("o"),
                Hourly = BuildHourlyForecast(payload.Hourly),
                Daily = BuildDailyForecast(payload.Daily),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] open-meteo fetch failed: {ex.Message}");
            return null;
        }
    }

    private static List<WeatherDailyForecast> BuildDailyForecast(OpenMeteoDaily? daily)
    {
        var rows = new List<WeatherDailyForecast>();
        if (daily?.Time is null || daily.Temperature2mMin is null || daily.Temperature2mMax is null)
            return rows;

        var count = Math.Min(7, Math.Min(daily.Time.Length, Math.Min(daily.Temperature2mMin.Length, daily.Temperature2mMax.Length)));
        for (var i = 0; i < count; i++)
        {
            var minC = daily.Temperature2mMin[i];
            var maxC = daily.Temperature2mMax[i];
            rows.Add(new WeatherDailyForecast
            {
                Date = daily.Time[i] ?? "",
                WeatherCode = daily.WeatherCode is not null && i < daily.WeatherCode.Length ? daily.WeatherCode[i] : -1,
                TemperatureMinC = minC,
                TemperatureMaxC = maxC,
                TemperatureMinF = minC * 9.0 / 5.0 + 32.0,
                TemperatureMaxF = maxC * 9.0 / 5.0 + 32.0,
            });
        }

        return rows;
    }

    private static List<WeatherHourlyForecast> BuildHourlyForecast(OpenMeteoHourly? hourly)
    {
        var rows = new List<WeatherHourlyForecast>();
        if (hourly?.Time is null || hourly.Temperature2m is null)
            return rows;

        var count = Math.Min(48, Math.Min(hourly.Time.Length, hourly.Temperature2m.Length));
        for (var i = 0; i < count; i++)
        {
            var tempC = hourly.Temperature2m[i];
            rows.Add(new WeatherHourlyForecast
            {
                Time = hourly.Time[i] ?? "",
                WeatherCode = hourly.WeatherCode is not null && i < hourly.WeatherCode.Length ? hourly.WeatherCode[i] : -1,
                TemperatureC = tempC,
                TemperatureF = tempC * 9.0 / 5.0 + 32.0,
            });
        }

        return rows;
    }

    private static string FormatLocation(IpLocation loc)
    {
        var city = loc.City ?? "";
        var region = loc.CountryCode ?? loc.Country ?? "";
        if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(region))
            return $"{city}, {region}";
        if (!string.IsNullOrEmpty(city))
            return city;
        if (!string.IsNullOrEmpty(region))
            return region;
        return "";
    }

    /// <summary>
    /// WMO weather code -> short human label. Matches the code ranges used on
    /// the widget side to pick an icon. See https://open-meteo.com/en/docs.
    /// </summary>
    public static string ConditionFor(int? code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "",
    };
}

public sealed class IpLocation
{
    [JsonPropertyName("latitude")] public double? Latitude { get; set; }
    [JsonPropertyName("longitude")] public double? Longitude { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
}

public sealed class OpenMeteoResponse
{
    [JsonPropertyName("current")] public OpenMeteoCurrent? Current { get; set; }
    [JsonPropertyName("hourly")] public OpenMeteoHourly? Hourly { get; set; }
    [JsonPropertyName("daily")] public OpenMeteoDaily? Daily { get; set; }
}

public sealed class OpenMeteoCurrent
{
    [JsonPropertyName("temperature_2m")] public double? Temperature2m { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double? RelativeHumidity2m { get; set; }
    [JsonPropertyName("weather_code")] public int? WeatherCode { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double? WindSpeed10m { get; set; }
}

public sealed class OpenMeteoHourly
{
    [JsonPropertyName("time")] public string[]? Time { get; set; }
    [JsonPropertyName("temperature_2m")] public double[]? Temperature2m { get; set; }
    [JsonPropertyName("weather_code")] public int[]? WeatherCode { get; set; }
}

public sealed class OpenMeteoDaily
{
    [JsonPropertyName("time")] public string[]? Time { get; set; }
    [JsonPropertyName("weather_code")] public int[]? WeatherCode { get; set; }
    [JsonPropertyName("temperature_2m_min")] public double[]? Temperature2mMin { get; set; }
    [JsonPropertyName("temperature_2m_max")] public double[]? Temperature2mMax { get; set; }
}
