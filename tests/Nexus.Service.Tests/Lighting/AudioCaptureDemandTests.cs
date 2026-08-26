using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Capture is otherwise gated on the LED engine running an audio effect, so a
/// client rendering an audio shader itself (Animate preview, media immersive
/// visualizer) sees all-zero uniforms unless its demand starts capture.
/// </summary>
public class AudioCaptureDemandTests : IDisposable
{
    private sealed class FakeBeats : IBeatsProvider
    {
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public void Start() { Running = true; Starts++; }
        public void Stop() => Running = false;
        public event Action? OnBeat { add { } remove { } }
        public void Dispose() { }
    }

    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly FakeBeats _beats = new();
    private readonly LightingProvider _provider;

    public AudioCaptureDemandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-audio-demand-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _engine = new LightingEngine();
        _hub = new LightingOutputHub();
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(
            _store, _engine, _hub, _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), beats: _beats);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Demand_StartsAndStopsCapture_WithNoAudioEffectLive()
    {
        _provider.ReconcileAudioCapture();
        Assert.False(_beats.Running);

        _provider.SetAudioCaptureDemand(true);
        Assert.True(_beats.Running);

        _provider.SetAudioCaptureDemand(false);
        Assert.False(_beats.Running);
    }

    [Fact]
    public void UnmatchedRelease_DoesNotSuppressALaterDemand()
    {
        _provider.SetAudioCaptureDemand(false);
        _provider.SetAudioCaptureDemand(false);

        _provider.SetAudioCaptureDemand(true);
        Assert.True(_beats.Running);
    }

    [Fact]
    public void ReleasingDemand_LeavesCaptureRunning_WhenMusicReactiveOwnsIt()
    {
        _provider.SetMusicReactive(true);
        _provider.StartAnimate(new Nexus.Service.Models.Lighting.AnimateHeadlessStart
        {
            Effect = "spectrumbars",
            Speed = 50,
            Intensity = 1f,
            Hue = 0f,
            Colorize = 0f,
            Saturation = 1f,
            Contrast = 1f,
            Persist = true,
            Params = new List<Nexus.Service.Models.Lighting.ShaderParam>(),
        });
        Assert.True(_beats.Running);

        _provider.SetAudioCaptureDemand(true);
        _provider.SetAudioCaptureDemand(false);
        Assert.True(_beats.Running);
    }
}
