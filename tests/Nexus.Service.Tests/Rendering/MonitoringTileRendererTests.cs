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
    [InlineData(MonitoringTileStyle.Segments, 72)]
    [InlineData(MonitoringTileStyle.Segments, 80)]
    [InlineData(MonitoringTileStyle.Segments, 96)]
    [InlineData(MonitoringTileStyle.Backdrop, 72)]
    [InlineData(MonitoringTileStyle.Backdrop, 80)]
    [InlineData(MonitoringTileStyle.Backdrop, 96)]
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
    [InlineData(MonitoringTileStyle.Line, 72)]
    [InlineData(MonitoringTileStyle.Line, 80)]
    [InlineData(MonitoringTileStyle.Line, 96)]
    [InlineData(MonitoringTileStyle.Segments, 72)]
    [InlineData(MonitoringTileStyle.Segments, 80)]
    [InlineData(MonitoringTileStyle.Segments, 96)]
    [InlineData(MonitoringTileStyle.Backdrop, 72)]
    [InlineData(MonitoringTileStyle.Backdrop, 80)]
    [InlineData(MonitoringTileStyle.Backdrop, 96)]
    [InlineData(MonitoringTileStyle.Number, 72)]
    [InlineData(MonitoringTileStyle.Number, 80)]
    [InlineData(MonitoringTileStyle.Number, 96)]
    public void Render_produces_a_non_empty_image_buffer_at_all_key_tile_sizes(MonitoringTileStyle style, int pixelSize)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU Usage",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, pixelSize);
        var rgba = RenderKit.ToRgba32Bytes(image);

        Assert.NotEmpty(rgba);
        Assert.Equal(pixelSize * pixelSize * 4, rgba.Length);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line)]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
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
    [InlineData(MonitoringTileStyle.Segments, 0x006c)] // XL: 96px JPEG
    [InlineData(MonitoringTileStyle.Backdrop, 0x006c)]
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

    [Theory]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
    public void Render_tolerates_empty_history_for_the_new_styles(MonitoringTileStyle style)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "0%",
            SensorType = "Load",
            History = System.Array.Empty<float>(),
            Style = style,
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
            Style = MonitoringTileStyle.Segments,
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

    [Theory]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
    public void Render_tolerates_a_degenerate_domain_for_the_new_styles(MonitoringTileStyle style)
    {
        var input = new MonitoringTileInput
        {
            Name = "Clock",
            ValueText = "4200MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4200f, 4200f },
            Style = style,
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

    [Fact]
    public void Render_segments_produces_different_pixels_from_line_at_the_same_input()
    {
        static MonitoringTileInput Input(MonitoringTileStyle style) => new()
        {
            Name = "CPU",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var line = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Line), 80);
        using var segments = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Segments), 80);

        Assert.NotEqual(RenderKit.ToRgb24(line), RenderKit.ToRgb24(segments));
    }

    // nexus-web's DeckMonitoringCell renders Backdrop as the same history
    // series, domain, and graph band as Line at full accent opacity with no
    // stroke; RenderLine already renders that way, so the two styles match
    // pixel for pixel on this renderer.
    [Fact]
    public void Render_backdrop_matches_line_pixel_for_pixel()
    {
        static MonitoringTileInput Input(MonitoringTileStyle style) => new()
        {
            Name = "CPU",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var line = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Line), 80);
        using var backdrop = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Backdrop), 80);

        Assert.Equal(RenderKit.ToRgb24(line), RenderKit.ToRgb24(backdrop));
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
    public void FillFraction_DividesByDomainMaxRatherThanMinMaxNormalizing()
    {
        var domain = (Min: 100f, Max: 200f);

        Assert.Equal(0.75f, MonitoringTileRenderer.FillFraction(150f, domain));
    }

    [Fact]
    public void FillFraction_ClampsAboveDomainMax()
    {
        Assert.Equal(1f, MonitoringTileRenderer.FillFraction(500f, (Min: 0f, Max: 100f)));
    }

    [Fact]
    public void FillFraction_DegenerateDomainRendersNeutralFill()
    {
        Assert.Equal(0.5f, MonitoringTileRenderer.FillFraction(50f, (Min: 10f, Max: 10f)));
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("radial")]
    public void ParseStyle_SegmentsAndLegacyRadialBothMapToSegments(string style)
    {
        Assert.Equal(MonitoringTileStyle.Segments, MonitoringTileRenderer.ParseStyle(style));
    }

    [Fact]
    public void ParseStyle_MapsBackdrop()
    {
        Assert.Equal(MonitoringTileStyle.Backdrop, MonitoringTileRenderer.ParseStyle("backdrop"));
    }

    [Fact]
    public void ParseStyle_MapsNumber()
    {
        Assert.Equal(MonitoringTileStyle.Number, MonitoringTileRenderer.ParseStyle("number"));
    }

    [Theory]
    [InlineData("line")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown-future-style")]
    public void ParseStyle_UnknownOrAbsentValuesFallBackToLine(string? style)
    {
        Assert.Equal(MonitoringTileStyle.Line, MonitoringTileRenderer.ParseStyle(style));
    }
}
