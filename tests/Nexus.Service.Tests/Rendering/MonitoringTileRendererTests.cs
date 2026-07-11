using System.Collections.Generic;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Rendering;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

public class MonitoringTileRendererTests
{
    private static readonly IReadOnlyList<float> History = new List<float> { 10f, 20f, 55f, 40f, 70f };

    [Theory]
    [InlineData(MonitoringTileStyle.Line, 72)]
    [InlineData(MonitoringTileStyle.Line, 80)]
    [InlineData(MonitoringTileStyle.Line, 96)]
    [InlineData(MonitoringTileStyle.Radial, 72)]
    [InlineData(MonitoringTileStyle.Radial, 80)]
    [InlineData(MonitoringTileStyle.Radial, 96)]
    [InlineData(MonitoringTileStyle.Number, 72)]
    [InlineData(MonitoringTileStyle.Number, 80)]
    [InlineData(MonitoringTileStyle.Number, 96)]
    public void Render_produces_a_square_image_at_the_requested_size(MonitoringTileStyle style, int pixelSize)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU Usage",
            ShowName = true,
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, pixelSize);

        Assert.Equal(pixelSize, image.Width);
        Assert.Equal(pixelSize, image.Height);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line)]
    [InlineData(MonitoringTileStyle.Radial)]
    [InlineData(MonitoringTileStyle.Number)]
    public void Render_encodes_as_a_valid_gen1_bmp_at_the_mini_key_size(MonitoringTileStyle style)
    {
        var model = StreamDeckModels.ByProductId(0x0063); // Mini: 80px BMP
        Assert.NotNull(model);

        var input = new MonitoringTileInput
        {
            Name = "GPU Temp",
            ValueText = "65°C",
            SensorType = "Temperature",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, model!.KeyPixelSize);
        var rgb = RenderKit.ToRgb24(image);
        var bmp = BmpEncoder.Encode(rgb, image.Width, image.Height);

        Assert.NotEmpty(bmp);
        Assert.True(model.IsValidWireImageLength(bmp.Length));
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line, 0x0080)] // MK.2: 72px JPEG
    [InlineData(MonitoringTileStyle.Radial, 0x006c)] // XL: 96px JPEG
    [InlineData(MonitoringTileStyle.Number, 0x0080)]
    public void Render_encodes_as_a_valid_gen2_jpeg(MonitoringTileStyle style, int productId)
    {
        var model = StreamDeckModels.ByProductId(productId);
        Assert.NotNull(model);

        var input = new MonitoringTileInput
        {
            Name = "Memory",
            ValueText = "48%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, model!.KeyPixelSize);
        var jpeg = RenderKit.EncodeJpeg(image);

        Assert.NotEmpty(jpeg);
        Assert.True(model.IsValidWireImageLength(jpeg.Length));
    }

    [Fact]
    public void Render_tolerates_empty_history()
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "0%",
            SensorType = "Load",
            History = System.Array.Empty<float>(),
            Style = MonitoringTileStyle.Line,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_tolerates_a_single_history_sample()
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "42%",
            SensorType = "Load",
            History = new List<float> { 42f },
            Style = MonitoringTileStyle.Radial,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_tolerates_a_degenerate_non_percent_domain_where_history_has_no_variation()
    {
        var input = new MonitoringTileInput
        {
            Name = "Clock",
            ValueText = "4200MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4200f, 4200f },
            Style = MonitoringTileStyle.Line,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_omits_the_name_when_ShowName_is_false()
    {
        var withName = new MonitoringTileInput { Name = "CPU", ShowName = true, ValueText = "1%", SensorType = "Load", History = History };
        var withoutName = new MonitoringTileInput { Name = "CPU", ShowName = false, ValueText = "1%", SensorType = "Load", History = History };

        using var imageWithName = MonitoringTileRenderer.Render(withName, 80);
        using var imageWithoutName = MonitoringTileRenderer.Render(withoutName, 80);

        Assert.Equal(80, imageWithName.Width);
        Assert.Equal(80, imageWithoutName.Width);
        Assert.NotEqual(RenderKit.ToRgb24(imageWithName), RenderKit.ToRgb24(imageWithoutName));
    }

    [Fact]
    public void Render_splits_the_number_style_value_into_a_numeric_and_unit_part_without_throwing()
    {
        var input = new MonitoringTileInput
        {
            Name = "GPU Clock",
            ValueText = "4713MHz",
            SensorType = "Clock",
            History = new List<float> { 4600f, 4700f, 4713f },
            Style = MonitoringTileStyle.Number,
        };

        using var image = MonitoringTileRenderer.Render(input, 96);

        Assert.Equal(96, image.Width);
    }

    [Theory]
    [InlineData("Load")]
    [InlineData("Temperature")]
    [InlineData("Control")]
    [InlineData("Level")]
    public void ResolveDomain_FixesPercentLikeSensorTypesTo0_100(string sensorType)
    {
        var domain = MonitoringTileRenderer.ResolveDomain(sensorType, new List<float> { 4200f, 4200f });

        Assert.Equal(0f, domain.Min);
        Assert.Equal(100f, domain.Max);
    }

    [Fact]
    public void ResolveDomain_AutoScalesEverythingElseToHistoryMinMax()
    {
        var domain = MonitoringTileRenderer.ResolveDomain("Clock", new List<float> { 4200f, 4700f, 4500f });

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Fact]
    public void RadialFraction_DividesByDomainMaxRatherThanMinMaxNormalizing()
    {
        var domain = (Min: 100f, Max: 200f);

        Assert.Equal(0.75f, MonitoringTileRenderer.RadialFraction(150f, domain));
    }

    [Fact]
    public void RadialFraction_ClampsAboveDomainMax()
    {
        Assert.Equal(1f, MonitoringTileRenderer.RadialFraction(500f, (Min: 0f, Max: 100f)));
    }

    [Fact]
    public void RadialFraction_DegenerateDomainRendersNeutralFill()
    {
        Assert.Equal(0.5f, MonitoringTileRenderer.RadialFraction(50f, (Min: 10f, Max: 10f)));
    }
}
