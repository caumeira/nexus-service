using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class LightingProviderPauseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;

    public LightingProviderPauseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-provider-pause-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _engine = new LightingEngine();
        _hub = new LightingOutputHub();
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_store, _engine, _hub, _gpu, new MediaLibrary(), new Nexus.Service.Platform.DefaultMonitorEnumerator());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SetPaused_NoActiveEffect_IsNoOp()
    {
        Assert.False(_provider.IsPaused);
        _provider.SetPaused(true);
        Assert.False(_provider.IsPaused);
    }

    [Fact]
    public void SetPaused_WithActiveEffect_Engages()
    {
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "plasma", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        _provider.SetPaused(true);
        Assert.True(_provider.IsPaused);
        _provider.SetPaused(false);
        Assert.False(_provider.IsPaused);
    }

    [Fact]
    public void StartAnimate_AfterPause_Resumes()
    {
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "plasma", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        _provider.SetPaused(true);
        Assert.True(_provider.IsPaused);

        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "fire", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        Assert.False(_provider.IsPaused);
    }

    [Fact]
    public void StartAnimate_SameEffectInPlaceUpdate_KeepsPaused()
    {
        // The in-place fast path (same effect name, e.g. a slider drag) updates
        // the running ShaderEffect's uniforms without calling LightingEngine.SetEffect,
        // so it must not clear an active pause.
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "plasma", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        _provider.SetPaused(true);
        Assert.True(_provider.IsPaused);

        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "plasma", Speed = 60, Intensity = 1f, Saturation = 1f, Contrast = 1f, Persist = false });
        Assert.True(_provider.IsPaused);
    }

    [Fact]
    public void StopAll_AfterPause_Resumes()
    {
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "plasma", Speed = 50, Intensity = 1f, Saturation = 1f, Contrast = 1f });
        _provider.SetPaused(true);
        Assert.True(_provider.IsPaused);

        _provider.StopAll();
        Assert.False(_provider.IsPaused);
    }
}
