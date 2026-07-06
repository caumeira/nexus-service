using Nexus.Service.Auth;
using Nexus.Service.Models.Weather;
using Nexus.Service.Platform.Weather;

namespace Nexus.Service.Routes;

public static class WeatherRoutes
{
    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weather", async (double? lat, double? lon, string? label, string? cc, IWeatherProvider provider) =>
            await provider.GetCurrentAsync(lat, lon, label, cc))
            .AllowPanel();

        app.MapGet("/api/weather/geocode", async (string? q, string? language, IWeatherProvider provider) =>
        {
            var results = await provider.SearchLocationsAsync(q ?? "", language);
            return new WeatherGeocodeResponse { Results = results };
        }).AllowPanel();
    }
}
