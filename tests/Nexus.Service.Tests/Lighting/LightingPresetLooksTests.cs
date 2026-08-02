using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingPresetLooksTests
{
    private static LightingSettings Live(string sync, string animateEffect) => new()
    {
        Sync = sync,
        Animate = new AnimateSettings { Effect = animateEffect },
    };

    private static AnimateEffectState State(float hue) => new()
    {
        Speed = 50,
        Intensity = 1f,
        Hue = hue,
        Colorize = 0f,
        Saturation = 1f,
        Contrast = 1f,
    };

    [Fact]
    public void Capture_takes_the_live_mode_and_effect()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.Animate.States["jellyfish"] = State(0.25f);

        var look = LightingPresetLooks.Capture(lighting);

        Assert.Equal("jellyfish", look.Sync);
        Assert.Equal("jellyfish", look.AnimateEffect);
        Assert.Equal(0.25f, look.AnimateState!.Hue);
    }

    [Fact]
    public void Capture_copies_the_state_so_a_later_live_edit_does_not_leak_in()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.Animate.States["jellyfish"] = State(0.25f);

        var look = LightingPresetLooks.Capture(lighting);
        lighting.Animate.States["jellyfish"].Hue = 0.9f;

        Assert.Equal(0.25f, look.AnimateState!.Hue);
    }

    [Fact]
    public void Capture_resolves_an_absent_state_through_the_selected_slot()
    {
        var lighting = Live("plasma", "plasma");
        lighting.Animate.Templates["plasma"] = new AnimateEffectTemplates
        {
            Selected = 1,
            Slots = new List<AnimateEffectState?> { State(0.1f), State(0.6f) },
        };

        var look = LightingPresetLooks.Capture(lighting);

        Assert.Equal(0.6f, look.AnimateState!.Hue);
        Assert.Equal(1, look.AnimateSlot);
    }

    [Fact]
    public void Apply_restores_the_mode_effect_and_look()
    {
        var lighting = Live("plasma", "plasma");
        var look = new LightingPresetLook
        {
            Sync = "jellyfish",
            AnimateEffect = "jellyfish",
            AnimateState = State(0.25f),
            AnimateSlot = 2,
        };

        LightingPresetLooks.Apply(lighting, look);

        Assert.Equal("jellyfish", lighting.Sync);
        Assert.Equal("jellyfish", lighting.Animate.Effect);
        Assert.Equal(0.25f, lighting.Animate.States["jellyfish"].Hue);
        Assert.Equal(2, lighting.Animate.Templates["jellyfish"].Selected);
    }

    // Two presets on one effect are the reported bug: States is keyed by effect,
    // so the second preset's colour must overwrite the first's on activate.
    [Fact]
    public void Apply_overwrites_a_colour_left_by_another_preset_on_the_same_effect()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.Animate.States["jellyfish"] = State(0.25f);

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "jellyfish",
            AnimateEffect = "jellyfish",
            AnimateState = State(0.75f),
        });

        Assert.Equal(0.75f, lighting.Animate.States["jellyfish"].Hue);
    }

    [Fact]
    public void Apply_drops_a_state_that_matches_the_selected_slot()
    {
        var lighting = Live("plasma", "plasma");
        lighting.Animate.Templates["plasma"] = new AnimateEffectTemplates
        {
            Selected = 0,
            Slots = new List<AnimateEffectState?> { State(0.4f) },
        };
        lighting.Animate.States["plasma"] = State(0.9f);

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "plasma",
            AnimateEffect = "plasma",
            AnimateState = State(0.4f),
            AnimateSlot = 0,
        });

        Assert.False(lighting.Animate.States.ContainsKey("plasma"));
    }

    [Fact]
    public void Capture_takes_the_master_brightness()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.GlobalBrightness = 0.4f;

        Assert.Equal(0.4f, LightingPresetLooks.Capture(lighting).GlobalBrightness);
    }

    [Fact]
    public void Apply_restores_the_master_brightness()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.GlobalBrightness = 1f;

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "jellyfish",
            AnimateEffect = "jellyfish",
            GlobalBrightness = 0.25f,
        });

        Assert.Equal(0.25f, lighting.GlobalBrightness);
    }

    // A look captured before brightness joined the preset carries null; treating
    // it as 0 would black out every device on activate.
    [Fact]
    public void Apply_leaves_the_master_brightness_alone_when_the_look_predates_it()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.GlobalBrightness = 0.8f;

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "jellyfish",
            AnimateEffect = "jellyfish",
            GlobalBrightness = null,
        });

        Assert.Equal(0.8f, lighting.GlobalBrightness);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(2f)]
    [InlineData(-1f)]
    public void Apply_rejects_a_non_finite_or_out_of_range_brightness(float stored)
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.GlobalBrightness = 0.5f;

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "jellyfish",
            AnimateEffect = "jellyfish",
            GlobalBrightness = stored,
        });

        Assert.InRange(lighting.GlobalBrightness, 0f, 1f);
        Assert.True(float.IsFinite(lighting.GlobalBrightness));
    }

    [Fact]
    public void Capture_tolerates_null_state_dictionaries()
    {
        var lighting = Live("jellyfish", "jellyfish");
        lighting.Animate.States = null!;
        lighting.Animate.Templates = null!;
        lighting.Static.States = null!;

        var look = LightingPresetLooks.Capture(lighting);

        Assert.Equal("jellyfish", look.AnimateEffect);
        Assert.Equal(-1, look.AnimateSlot);
    }

    [Fact]
    public void Capture_and_apply_carry_the_media_clip()
    {
        var lighting = Live("media", "jellyfish");
        lighting.LastMediaId = "clip-a";

        var look = LightingPresetLooks.Capture(lighting);
        lighting.LastMediaId = "clip-b";
        LightingPresetLooks.Apply(lighting, look);

        Assert.Equal("clip-a", lighting.LastMediaId);
    }

    [Fact]
    public void Capture_and_apply_carry_the_screen_and_media_filters()
    {
        var lighting = Live("screen", "jellyfish");
        lighting.ScreenEffect = new PostProcessSettings { Hue = 0.3f, Saturation = 1.4f, Reactive = true };
        lighting.MediaEffect = new PostProcessSettings { Hue = 0.7f, FlipX = true };

        var look = LightingPresetLooks.Capture(lighting);
        lighting.ScreenEffect = new PostProcessSettings { Hue = 0.9f };
        lighting.MediaEffect = new PostProcessSettings { Hue = 0.1f };
        LightingPresetLooks.Apply(lighting, look);

        Assert.Equal(0.3f, lighting.ScreenEffect.Hue);
        Assert.Equal(1.4f, lighting.ScreenEffect.Saturation);
        Assert.True(lighting.ScreenEffect.Reactive);
        Assert.Equal(0.7f, lighting.MediaEffect.Hue);
        Assert.True(lighting.MediaEffect.FlipX);
    }

    [Fact]
    public void Capture_copies_the_filters_so_a_later_live_edit_does_not_leak_in()
    {
        var lighting = Live("screen", "jellyfish");
        lighting.ScreenEffect = new PostProcessSettings { Hue = 0.3f };

        var look = LightingPresetLooks.Capture(lighting);
        lighting.ScreenEffect.Hue = 0.9f;

        Assert.Equal(0.3f, look.ScreenEffect!.Hue);
    }

    // A look captured before the filters joined the preset carries null; the
    // live filter must survive rather than resetting to defaults.
    [Fact]
    public void Apply_leaves_the_filters_alone_when_the_look_predates_them()
    {
        var lighting = Live("screen", "jellyfish");
        lighting.ScreenEffect = new PostProcessSettings { Hue = 0.6f };

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "screen",
            ScreenEffect = null,
            MediaEffect = null,
        });

        Assert.Equal(0.6f, lighting.ScreenEffect.Hue);
    }

    [Fact]
    public void Apply_rejects_a_non_finite_filter_value()
    {
        var lighting = Live("screen", "jellyfish");

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "screen",
            ScreenEffect = new PostProcessSettings { Hue = float.NaN, Saturation = float.PositiveInfinity },
        });

        Assert.True(float.IsFinite(lighting.ScreenEffect.Hue));
        Assert.True(float.IsFinite(lighting.ScreenEffect.Saturation));
    }

    [Fact]
    public void Apply_restores_the_static_selection()
    {
        var lighting = Live("jellyfish", "jellyfish");

        LightingPresetLooks.Apply(lighting, new LightingPresetLook
        {
            Sync = "static",
            StaticEffect = "simplered",
            StaticState = State(0f),
        });

        Assert.Equal("static", lighting.Sync);
        Assert.Equal("simplered", lighting.Static.Effect);
    }

    [Fact]
    public void CaptureIntoActive_is_a_no_op_without_a_selected_preset()
    {
        var settings = new NexusSettings { Lighting = Live("jellyfish", "jellyfish") };
        settings.Lighting.LayoutPresets.Add(new LayoutPreset { Id = "p1", Name = "One" });

        LightingPresetLooks.CaptureIntoActive(settings);

        Assert.Null(settings.Lighting.LayoutPresets[0].Look);
    }

    [Fact]
    public void CaptureIntoActive_writes_the_live_selection_into_the_selected_preset()
    {
        var settings = new NexusSettings { Lighting = Live("jellyfish", "jellyfish") };
        settings.Lighting.LayoutPresets.Add(new LayoutPreset { Id = "p1", Name = "One" });
        settings.Lighting.ActiveLayoutPresetId = "p1";

        LightingPresetLooks.CaptureIntoActive(settings);

        Assert.Equal("jellyfish", settings.Lighting.LayoutPresets[0].Look!.Sync);
    }

    [Fact]
    public void CaptureIntoActive_ignores_an_id_that_no_longer_resolves()
    {
        var settings = new NexusSettings { Lighting = Live("jellyfish", "jellyfish") };
        settings.Lighting.ActiveLayoutPresetId = "deleted";

        LightingPresetLooks.CaptureIntoActive(settings);

        Assert.Empty(settings.Lighting.LayoutPresets);
    }
}
