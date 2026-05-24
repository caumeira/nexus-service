using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the two behaviours the slider-drag hot path relies on:
///  1. Consecutive StartAnimate calls with the SAME effect name reuse the
///     existing ShaderEffect instance (no re-alloc, no re-compile).
///  2. StartAnimate with Persist=false updates the engine but skips the
///     settings.json write so slider drags don't hammer the disk.
/// </summary>
public class LightingProviderFastPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;

    public LightingProviderFastPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-provider-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new JsonConfigStore(_settingsPath);
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

    private static AnimateHeadlessStart MakeBody(string effect, int speed, bool persist, float hue = 0f) =>
        new()
        {
            Effect = effect,
            Speed = speed,
            Intensity = 1f,
            Hue = hue,
            Colorize = 0f,
            Saturation = 1f,
            Contrast = 1f,
            Persist = persist,
            Params = new List<ShaderParam>(),
        };

    [Fact]
    public void SameEffectName_ReusesInstance_DoesNotReallocate()
    {
        _provider.StartAnimate(MakeBody("plasma", 50, persist: true));
        var firstInstance = _engine.CurrentEffect;
        Assert.NotNull(firstInstance);

        // Fire 20 more StartAnimate calls with the same effect name, like a
        // slider drag would. The underlying ShaderEffect reference must stay
        // identical -- that's the whole point of the in-place update path
        // that eliminated ~86 KB of buffer churn per drag.
        for (int i = 0; i < 20; i++)
        {
            _provider.StartAnimate(MakeBody("plasma", 40 + i, persist: false));
            Assert.Same(firstInstance, _engine.CurrentEffect);
        }
    }

    [Fact]
    public void DifferentEffectName_SwapsInstance()
    {
        _provider.StartAnimate(MakeBody("plasma", 50, persist: true));
        var first = _engine.CurrentEffect;
        _provider.StartAnimate(MakeBody("fire", 50, persist: true));
        var second = _engine.CurrentEffect;
        Assert.NotSame(first, second);
        Assert.Equal("fire", _engine.CurrentEffectName);
    }

    [Fact]
    public void Persist_False_SkipsSettingsWrite()
    {
        // Baseline: one persisted call writes the file.
        _provider.StartAnimate(MakeBody("plasma", 50, persist: true));
        _store.FlushNow();
        Assert.True(File.Exists(_settingsPath));
        var baselineMtime = File.GetLastWriteTimeUtc(_settingsPath);
        var baselineLen = new FileInfo(_settingsPath).Length;

        // 50 non-persist updates must NOT change the file (no Update call,
        // so no dirty flag, so nothing for FlushNow to write).
        for (int i = 0; i < 50; i++)
        {
            _provider.StartAnimate(MakeBody("plasma", 10 + i, persist: false, hue: i / 50f));
        }
        _store.FlushNow();

        Assert.Equal(baselineMtime, File.GetLastWriteTimeUtc(_settingsPath));
        Assert.Equal(baselineLen, new FileInfo(_settingsPath).Length);
    }

    [Fact]
    public void Persist_True_UpdatesStoredEffectState()
    {
        _provider.StartAnimate(MakeBody("plasma", 75, persist: true, hue: 0.5f));
        _store.FlushNow();

        var settings = _store.Load();
        Assert.Equal("plasma", settings.Lighting.Sync);
        Assert.Equal("plasma", settings.Lighting.Animate.Effect);
        Assert.True(settings.Lighting.Animate.States.ContainsKey("plasma"));
        Assert.Equal(75, settings.Lighting.Animate.States["plasma"].Speed);
        Assert.Equal(0.5f, settings.Lighting.Animate.States["plasma"].Hue);
    }
}
