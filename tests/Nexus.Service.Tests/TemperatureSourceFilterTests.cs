using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class TemperatureSourceFilterTests
{
    [Theory]
    [InlineData(-55f, false)]   // Linux unconnected ITE SuperIO sentinel
    [InlineData(0f, false)]     // disabled channel
    [InlineData(0.25f, false)]  // Windows unpopulated DIMM SPD temp
    [InlineData(1f, false)]     // boundary: <= 1°C is implausible
    [InlineData(1.01f, true)]   // just above the floor
    [InlineData(30f, true)]     // real GPU idle
    [InlineData(85f, true)]     // real hot DIMM/storage
    public void IsPlausible_MatchesExpected(float celsius, bool expected)
        => Assert.Equal(expected, TemperatureSourceFilter.IsPlausible(celsius));
}
