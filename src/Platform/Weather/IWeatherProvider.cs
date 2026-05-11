using System.Threading.Tasks;
using Qos.Service.Models.Weather;

namespace Qos.Service.Platform.Weather;

/// <summary>
/// Returns a current weather snapshot. Never throws - returns
/// <see cref="WeatherSnapshot.Empty"/> on any failure so callers can render a
/// "no data" state without a try/catch.
/// </summary>
public interface IWeatherProvider
{
    Task<WeatherSnapshot> GetCurrentAsync();
}
