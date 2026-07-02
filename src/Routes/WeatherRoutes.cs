using Nexus.Service.Auth;
using Nexus.Service.Platform.Weather;

namespace Nexus.Service.Routes;

public static class WeatherRoutes
{
    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weather", async (IWeatherProvider provider) =>
            await provider.GetCurrentAsync())
            .AllowPanel();
    }
}
