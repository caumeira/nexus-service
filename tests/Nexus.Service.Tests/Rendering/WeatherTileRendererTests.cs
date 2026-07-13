using Nexus.Service.Rendering;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

public class WeatherTileRendererTests
{
    [Theory]
    [InlineData(0, 72)]
    [InlineData(2, 80)]
    [InlineData(63, 96)]
    [InlineData(73, 72)]
    [InlineData(-1, 80)]
    public void Render_produces_a_square_non_empty_image_for_every_condition_group(int weatherCode, int pixelSize)
    {
        var input = new WeatherTileInput
        {
            TemperatureText = "72°F",
            LocationLabel = "San Francisco",
            WeatherCode = weatherCode,
        };

        using var image = WeatherTileRenderer.Render(input, pixelSize);

        Assert.Equal(pixelSize, image.Width);
        Assert.Equal(pixelSize, image.Height);
    }

    [Fact]
    public void Render_withEmptyTextFields_stillProducesAnImageWithoutThrowing()
    {
        var input = new WeatherTileInput();

        using var image = WeatherTileRenderer.Render(input, 72);

        Assert.Equal(72, image.Width);
        Assert.Equal(72, image.Height);
    }
}
