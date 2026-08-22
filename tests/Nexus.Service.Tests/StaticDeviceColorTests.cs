using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

/// <summary>
/// Per-device Static assignments have to survive the canvas sample, which is the
/// step that previously overwrote them, and they must render the ASSIGNED
/// EFFECT - a flat colour cannot represent a gradient or carry the tint
/// controls, which is how the first cut of this shipped wrong.
/// </summary>
public class StaticDeviceEffectTests
{
    private sealed class FillEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public FillEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "fill";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                    canvas.SetPixel(x, y, _r, _g, _b);
        }
        public void Dispose() { }
    }

    /// <summary>Red on the left edge, blue on the right - a horizontal ramp.</summary>
    private sealed class RampEffect : IEffect
    {
        public string Name => "ramp";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                {
                    var t = canvas.Width > 1 ? (float)x / (canvas.Width - 1) : 0f;
                    canvas.SetPixel(x, y, (byte)(255 * (1 - t)), 0, (byte)(255 * t));
                }
        }
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(
        DeviceFrame device,
        StaticDeviceEffectTracker? tracker,
        Func<StaticDeviceAssignment, IEffect?>? factory)
    {
        using var engine = new LightingEngine { StaticEffects = tracker, StaticEffectFactory = factory };
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new FillEffect(0, 255, 0));   // shared canvas = green
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    private static DeviceFrame MakeDevice(int leds = 5) =>
        new(0, "keeb:keys", leds, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    private static StaticDeviceAssignment Assign(string effect) => new() { Effect = effect };

    [Fact]
    public async Task Assigned_effect_overrides_the_shared_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
    }

    [Fact]
    public async Task A_gradient_assignment_paints_DIFFERENT_leds_across_the_device()
    {
        // The flat-colour implementation this replaces could only ever produce
        // one colour here; a ramp must read end to end (fullscreen evaluation).
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("ramp"));
        // Wider than tall, so its LEDs run ACROSS the canvas and meet the sweep.
        var wide = new DeviceFrame(0, "keeb:keys", 5, x: 100, y: 100, w: 250, h: 40, rotation: 0);
        var leds = await RenderOnce(wide, tracker, _ => new RampEffect());
        var first = (leds[0], leds[1], leds[2]);
        var last = (leds[12], leds[13], leds[14]);
        Assert.NotEqual(first, last);
        Assert.True(leds[0] > leds[12], "left end should be redder than the right");
        Assert.True(leds[14] > leds[2], "right end should be bluer than the left");
    }

    [Fact]
    public async Task Assignment_is_ignored_outside_static_mode()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = false };
        tracker.Set("keeb:keys", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(0, leds[0]);
        Assert.Equal(255, leds[1]);   // shared canvas green
    }

    [Fact]
    public async Task Unassigned_device_still_samples_the_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("someone-else", Assign("red"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => new FillEffect(255, 0, 0));
        Assert.Equal(255, leds[1]);
    }

    [Fact]
    public async Task A_factory_that_throws_falls_back_to_the_canvas()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("boom"));
        var leds = await RenderOnce(MakeDevice(), tracker, _ => throw new InvalidOperationException("no gpu"));
        Assert.Equal(255, leds[1]);
    }

    /// <summary>Blue at the top, red at the bottom - a VERTICAL sweep.</summary>
    private sealed class VerticalRampEffect : IEffect
    {
        public string Name => "vramp";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (int y = 0; y < canvas.Height; y++)
                for (int x = 0; x < canvas.Width; x++)
                {
                    var t = canvas.Height > 1 ? (float)y / (canvas.Height - 1) : 0f;
                    canvas.SetPixel(x, y, (byte)(255 * t), 0, (byte)(255 * (1 - t)));
                }
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_vertically_swept_pattern_reads_along_a_tall_device()
    {
        // Sampling a fixed horizontal midline collapsed this to one colour on
        // the bench: every LED of a tall strip read the same pixel.
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        tracker.Set("keeb:keys", Assign("vramp"));
        // Taller than wide, so its LEDs run down the canvas.
        var tall = new DeviceFrame(0, "keeb:keys", 5, x: 300, y: 100, w: 40, h: 250, rotation: 0);
        var leds = await RenderOnce(tall, tracker, _ => new VerticalRampEffect());
        Assert.NotEqual((leds[0], leds[1], leds[2]), (leds[12], leds[13], leds[14]));
        Assert.True(leds[14] < leds[2], "bottom end should be less blue than the top");
    }

    [Fact]
    public void Key_changes_when_any_control_moves()
    {
        var baseline = new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f };
        Assert.Equal(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.6f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment { Effect = "gradientlinear", Hue = 0.5f, Colorize = 0.3f }.Key());
        Assert.NotEqual(baseline.Key(), new StaticDeviceAssignment
        {
            Effect = "gradientlinear", Hue = 0.5f,
            Params = new Dictionary<string, float> { ["u_angle"] = 90f },
        }.Key());
    }

    [Fact]
    public void Version_bumps_so_the_engine_drops_stale_renders()
    {
        var tracker = new StaticDeviceEffectTracker { Enabled = true };
        var v0 = tracker.Version;
        tracker.Set("a", Assign("x"));
        Assert.NotEqual(v0, tracker.Version);
        var v1 = tracker.Version;
        tracker.Clear("a");
        Assert.NotEqual(v1, tracker.Version);
    }

    /// <summary>
    /// An assignment is what the hardware is meant to show, so it must survive a
    /// restart. Without this the UI still listed every pick (it keeps its own
    /// copy) while the devices fell back to the shared canvas on entering
    /// Static - which is exactly how this surfaced on the bench.
    /// </summary>
    [Fact]
    public void Assignments_are_persisted_and_rehydrated()
    {
        var store = new InMemoryConfigStore();
        var first = new StaticDeviceEffectTracker(store) { Enabled = true };
        first.Set("keeb:keys", new StaticDeviceAssignment
        {
            Effect = "gradientlinear",
            Colorize = 0.25f,
            Params = new Dictionary<string, float> { ["u_angle"] = 90f },
        });

        // A fresh tracker over the same store is the restart.
        var reborn = new StaticDeviceEffectTracker(store) { Enabled = true };
        Assert.True(reborn.TryGet("keeb:keys", out var back));
        Assert.Equal("gradientlinear", back.Effect);
        Assert.Equal(0.25f, back.Colorize, 3);
        Assert.NotNull(back.Params);
        Assert.Equal(90f, back.Params!["u_angle"], 3);
        Assert.Equal(first.TryGet("keeb:keys", out var orig) ? orig.Key() : "", back.Key());
    }

    [Fact]
    public void Clearing_an_assignment_removes_it_from_the_store_too()
    {
        var store = new InMemoryConfigStore();
        var tracker = new StaticDeviceEffectTracker(store) { Enabled = true };
        tracker.Set("keeb:keys", new StaticDeviceAssignment { Effect = "simplered" });
        tracker.Clear("keeb:keys");
        Assert.Empty(store.Load().Lighting.StaticDeviceLooks);
        Assert.False(new StaticDeviceEffectTracker(store) { Enabled = true }.TryGet("keeb:keys", out _));
    }
}
