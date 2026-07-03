using System;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRoutesTests
{
    private static readonly TryxOverlayConfig CurrentOverlay = new()
    {
        Align = "Right",
        Filter = "blur",
        Opacity = 80,
    };

    [Fact]
    public void BuildOverlayConfigFromRequest_maps_items_to_stats_and_positions_in_order()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { Stat = "CPU Temperature", X = 0.03, Y = 0.10 },
                new TryxOverlayItem { Stat = "GPU Temperature", X = 0.50, Y = 0.60 },
            ],
            Font = "roboto-bold",
            Size = 120,
            Color = "#112233",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(["CPU Temperature", "GPU Temperature"], cfg.Stats);
        Assert.Equal([0.03, 0.50], cfg.PosX);
        Assert.Equal([0.10, 0.60], cfg.PosY);
        Assert.Equal("roboto-bold", cfg.Font);
        Assert.Equal(120, cfg.Size);
        Assert.Equal("#112233", cfg.Color);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_truncates_more_than_three_items()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { Stat = "CPU Temperature", X = 0, Y = 0 },
                new TryxOverlayItem { Stat = "GPU Temperature", X = 0, Y = 0.1 },
                new TryxOverlayItem { Stat = "CPU Usage", X = 0, Y = 0.2 },
                new TryxOverlayItem { Stat = "GPU Usage", X = 0, Y = 0.3 },
            ],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(3, cfg.Stats.Length);
        Assert.Equal(["CPU Temperature", "GPU Temperature", "CPU Usage"], cfg.Stats);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_skips_blank_stat_names()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { Stat = "", X = 0, Y = 0 },
                new TryxOverlayItem { Stat = "   ", X = 0, Y = 0 },
                new TryxOverlayItem { Stat = "CPU Temperature", X = 0.1, Y = 0.2 },
            ],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(["CPU Temperature"], cfg.Stats);
        Assert.Equal([0.1], cfg.PosX);
        Assert.Equal([0.2], cfg.PosY);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    public void BuildOverlayConfigFromRequest_clamps_x_and_y_to_0_1(double input, double clamped)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { Stat = "CPU Temperature", X = input, Y = input }],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(clamped, cfg.PosX[0]);
        Assert.Equal(clamped, cfg.PosY[0]);
    }

    [Theory]
    [InlineData("roboto-bold", "roboto-bold")]
    [InlineData("monospace", "monospace")]
    [InlineData("Comic Sans MS", "roboto-regular")]
    [InlineData("", "roboto-regular")]
    public void BuildOverlayConfigFromRequest_falls_back_to_roboto_regular_for_an_invalid_font(
        string requestedFont, string expectedFont)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { Stat = "CPU Temperature", X = 0, Y = 0 }],
            Font = requestedFont,
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(expectedFont, cfg.Font);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(150, 150)]
    [InlineData(200, 150)]
    public void BuildOverlayConfigFromRequest_clamps_size_to_50_150(int input, int clamped)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { Stat = "CPU Temperature", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = input,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(clamped, cfg.Size);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_defaults_a_blank_color_to_white()
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { Stat = "CPU Temperature", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = 100,
            Color = "  ",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal("#ffffff", cfg.Color);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_preserves_align_filter_and_opacity_from_the_current_overlay()
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { Stat = "CPU Temperature", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal("Right", cfg.Align);
        Assert.Equal("blur", cfg.Filter);
        Assert.Equal(80, cfg.Opacity);
    }

    // ── /tryx/status overlay.items round-trip ──

    [Fact]
    public void BuildOverlayItems_pairs_each_stat_with_its_configured_position()
    {
        var overlay = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature", "GPU Temperature"],
            PosX = [0.03, 0.50],
            PosY = [0.10, 0.60],
        };

        var items = TryxRoutes.BuildOverlayItems(overlay);

        Assert.Equal(2, items.Length);
        Assert.Equal("CPU Temperature", items[0].Stat);
        Assert.Equal(0.03, items[0].X);
        Assert.Equal(0.10, items[0].Y);
        Assert.Equal("GPU Temperature", items[1].Stat);
        Assert.Equal(0.50, items[1].X);
        Assert.Equal(0.60, items[1].Y);
    }

    [Fact]
    public void BuildOverlayItems_reports_the_fallback_stack_for_a_stat_with_no_saved_position()
    {
        var overlay = new TryxOverlayConfig
        {
            Stats = ["CPU Temperature", "GPU Temperature"],
            PosX = [],
            PosY = [],
        };

        var items = TryxRoutes.BuildOverlayItems(overlay);

        Assert.Equal(0.04, items[0].X);
        Assert.Equal(0.12, items[0].Y);
        Assert.Equal(0.04, items[1].X);
        Assert.Equal(0.28, items[1].Y);
    }

    [Fact]
    public void ResolveAvailablePresets_returns_only_the_ids_the_panel_reported()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "default_02" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.Equal("default_02", presets[1].Id);
        Assert.All(presets, p => Assert.False(string.IsNullOrEmpty(p.Name)));
    }

    [Fact]
    public void ResolveAvailablePresets_falls_back_to_the_first_six_when_the_panel_has_not_reported_yet()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(Array.Empty<string>());

        Assert.Equal(6, presets.Count);
        Assert.Equal(
            new[] { "default_01", "default_02", "default_03", "default_04", "default_05", "default_06" },
            presets.ConvertAll(p => p.Id));
    }

    [Fact]
    public void ResolveAvailablePresets_ignores_ids_that_are_not_in_the_known_catalog()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "start", "screensaver" });

        Assert.Single(presets);
        Assert.Equal("default_01", presets[0].Id);
    }

    [Fact]
    public void ResolveAvailablePresets_lists_an_uncataloged_default_wallpaper_under_its_raw_id()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "default_07" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.NotEqual("default_01", presets[0].Name);
        Assert.Equal("default_07", presets[1].Id);
        Assert.Equal("default_07", presets[1].Name);
    }
}
