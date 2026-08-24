using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class LightingSleepBlackoutTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;

    public LightingSleepBlackoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-sleep-blackout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DeviceFrame[] MakeLitDevices(int count = 2, int leds = 4)
    {
        var frames = new DeviceFrame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new DeviceFrame(i, $"test-{i}", leds);
            frames[i].Fill(255, 128, 64);
            frames[i].Publish();
        }
        return frames;
    }

    private static bool AllBlack(DeviceFrame frame)
    {
        foreach (var b in frame.LedBytes)
        {
            if (b != 0) return false;
        }
        return true;
    }

    [Fact]
    public void SleepBlackout_DefaultsOn()
    {
        Assert.True(_store.Load().Lighting.SleepBlackout);
    }

    [Fact]
    public void SetBlackout_WithNoEffectRunning_BlanksImmediately()
    {
        // The case the feature exists for: nothing is rendering, yet RAM is
        // still showing whatever it was last written.
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        Assert.False(AllBlack(devices[0]));

        engine.SetBlackout(true);

        Assert.True(engine.Blackout);
        Assert.True(engine.WaitForBlackout(TimeSpan.Zero));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task SetBlackout_WithEffectRunning_LoopPublishesBlack()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        engine.SetEffect(new FillEffect(255, 255, 255));

        // Let the effect paint at least one lit frame first.
        await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2));

        engine.SetBlackout(true);

        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));
        Assert.All(devices, d => Assert.True(AllBlack(d)));
    }

    [Fact]
    public async Task ReleasingBlackout_RepaintsFromTheSameEffect()
    {
        using var engine = new LightingEngine();
        engine.FrameIntervalMs = 5;
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var effect = new FillEffect(255, 255, 255);
        engine.SetEffect(effect);
        engine.SetBlackout(true);
        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(2)));

        engine.SetBlackout(false);

        Assert.False(engine.Blackout);
        // Same effect instance, never swapped out - the release is a resume,
        // not a restart.
        Assert.Same(effect, engine.CurrentEffect);
        Assert.True(await WaitUntil(() => !AllBlack(devices[0]), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SetEffect_ClearsBlackout()
    {
        // The escape hatch: if a resume event never lands, the user picking a
        // mode has to give them their lighting back.
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        engine.SetBlackout(true);
        Assert.True(engine.Blackout);

        engine.SetEffect(new FillEffect(1, 2, 3));

        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_WhenSettingOff_LeavesLightingAlone()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        _store.Update(s => s.Lighting.SleepBlackout = false);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();

        Assert.False(engine.Blackout);
        Assert.False(AllBlack(devices[0]));
    }

    [Fact]
    public void Coordinator_WhenSettingOn_BlanksOnSuspendAndRestoresOnResume()
    {
        using var engine = new LightingEngine();
        var devices = MakeLitDevices();
        engine.UpdateDevices(devices);
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();
        Assert.True(engine.Blackout);
        Assert.All(devices, d => Assert.True(AllBlack(d)));

        coordinator.OnResumed();
        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_OnResumed_WithoutASuspend_IsANoOp()
    {
        // Resume fires for wake sources that never suspended lighting (and the
        // route calls it when the setting is switched off); it must not disturb
        // a blackout it did not engage, nor throw.
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnResumed();

        Assert.False(engine.Blackout);
    }

    [Fact]
    public void Coordinator_SuspendTwice_StaysBlacked()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        var coordinator = new SleepBlackoutCoordinator(engine, _store);

        coordinator.OnSuspending();
        coordinator.OnSuspending();

        Assert.True(engine.Blackout);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // The test host disposes the DI scope and then the factory, so the
        // engine sees two Dispose calls; the second reaches Stop(), which
        // touches the blackout signal.
        var engine = new LightingEngine();
        engine.UpdateDevices(MakeLitDevices());
        engine.Dispose();

        engine.Dispose();
        engine.Stop();
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }

    private sealed class FillEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public FillEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "fill";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(_r, _g, _b);
        public void Dispose() { }
    }
}
