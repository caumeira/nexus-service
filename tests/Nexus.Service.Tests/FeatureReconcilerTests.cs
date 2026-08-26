using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// FeatureReconciler.Apply is the one-time transition side effect fired from
/// PATCH /preferences; per-tick gating lives in the workers themselves. These
/// tests drive Apply directly against recording fakes, the same shape
/// McpTestHarness/DeckActionExecutorTests use for ILightingProvider.
/// </summary>
public class FeatureReconcilerTests
{
    private sealed class RecordingLightingProvider : ILightingProvider
    {
        public List<AnimateHeadlessStart> StartAnimateCalls { get; } = new();
        public int SuspendCallCount { get; private set; }
        public int StopAllCallCount { get; private set; }

        public string GetSync() => "none";
        public void SetSync(string sync) { }
        public void StopAll() => StopAllCallCount++;
        public void Suspend() => SuspendCallCount++;
        public bool IsPaused => false;
        public void SetPaused(bool paused) { }
        public void SetBrightness(BrightnessScale scale) { }
        public void SetSpeed(SpeedScale scale) { }
        public AnimateOptions GetAnimateOptions() => new();
        public AudioSyncOptions GetAudioSyncOptions() => new();
        public ScreenSyncOptions GetScreenSyncOptions() => new();
        public void StartAnimate(AnimateHeadlessStart body) => StartAnimateCalls.Add(body);
        public void StartStatic(StaticHeadlessStart body) { }
        public void StartMusic(MusicHeadlessStart body) { }
        public void StartScreen(ScreenHeadlessStart body) { }
        public void ReselectScreen() { }
        public bool StartMedia(string mediaId) => false;
        public void StartMediaIdle() { }
        public void StartGameSync() { }
        public Nexus.Service.Lighting.Engine.Effects.GameSyncEffect? ActiveGameSyncEffect() => null;
        public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f) { }
        public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist) { }
        public (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false, bool frozen = false) => null;
        public void SaveAnimateTemplates(Dictionary<string, AnimateEffectTemplates> templates) { }
        public void SetMusicReactive(bool enabled) { }
        public void ReconcileAudioCapture() { }
        public void SetAudioCaptureDemand(bool demanded) { }
    }

    private sealed class RecordingFanControlProvider : IFanControlProvider
    {
        public int ReleaseAllCallCount { get; private set; }

        public IReadOnlyList<FanChannel> GetFanChannels() => System.Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => System.Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() => ReleaseAllCallCount++;
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, System.IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(System.Array.Empty<FanCalibration>());
    }

    // No real HID device: KeebSettingsApplier.Apply() reads _hub.IsConnected
    // (false) and returns without writing, so the OFF->ON path exercises the
    // call without touching hardware.
    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => System.Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    private static (FeatureReconciler Reconciler, RecordingLightingProvider Lighting, RecordingFanControlProvider Fans, InMemoryConfigStore Store) Build()
    {
        var store = new InMemoryConfigStore();
        var lighting = new RecordingLightingProvider();
        var fans = new RecordingFanControlProvider();
        var keeb = new KeebSettingsApplier(new KeebHub(new NoDevices()), store, new Nexus.Service.Sockets.MultiplexHub());
        var reconciler = new FeatureReconciler(lighting, fans, store, keeb);
        return (reconciler, lighting, fans, store);
    }

    [Fact]
    public void Apply_LightingOffToOn_ReplaysPersistedSync()
    {
        var (reconciler, lighting, _, store) = Build();
        store.Update(s => s.Lighting.Sync = "plasma");
        var before = new FeaturesSettings { Lighting = false, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        var call = Assert.Single(lighting.StartAnimateCalls);
        Assert.Equal("plasma", call.Effect);
        Assert.Equal(0, lighting.SuspendCallCount);
    }

    [Fact]
    public void Apply_LightingOnToOff_SuspendsOnce()
    {
        var (reconciler, lighting, _, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = false, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(1, lighting.SuspendCallCount);
        Assert.Equal(0, lighting.StopAllCallCount);
        Assert.Empty(lighting.StartAnimateCalls);
    }

    [Fact]
    public void Apply_CoolingOnToOff_ReleasesOnce()
    {
        var (reconciler, _, fans, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(1, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void Apply_CoolingOffToOn_DoesNotReleaseOrDriveFans()
    {
        var (reconciler, _, fans, _) = Build();
        var before = new FeaturesSettings { Lighting = true, Cooling = false, Monitoring = true, Diagnostics = true };
        var after = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(before, after);

        Assert.Equal(0, fans.ReleaseAllCallCount);
    }

    [Fact]
    public void Apply_NoTransition_TouchesNothing()
    {
        var (reconciler, lighting, fans, _) = Build();
        var same = new FeaturesSettings { Lighting = true, Cooling = true, Monitoring = true, Diagnostics = true };

        reconciler.Apply(same, same);

        Assert.Equal(0, lighting.SuspendCallCount);
        Assert.Equal(0, lighting.StopAllCallCount);
        Assert.Empty(lighting.StartAnimateCalls);
        Assert.Equal(0, fans.ReleaseAllCallCount);
    }
}
