using Qos.Service.Auth;
using Qos.Service.Platform.Weather;

namespace Qos.Service.Routes;

public static class WeatherRoutes
{
    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weather", async (IWeatherProvider provider) =>
            await provider.GetCurrentAsync())
            .AllowPanel();
    }
}
