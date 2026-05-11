using System.Threading.Tasks;
using Qos.Service.Models.Weather;

namespace Qos.Service.Platform.Weather;

public sealed class StubWeatherProvider : IWeatherProvider
{
    public Task<WeatherSnapshot> GetCurrentAsync() => Task.FromResult(WeatherSnapshot.Empty);
}
