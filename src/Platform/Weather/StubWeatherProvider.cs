using System.Threading.Tasks;
using Nexus.Service.Models.Weather;

namespace Nexus.Service.Platform.Weather;

public sealed class StubWeatherProvider : IWeatherProvider
{
    public Task<WeatherSnapshot> GetCurrentAsync() => Task.FromResult(WeatherSnapshot.Empty);
}
