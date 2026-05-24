using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the Screen Mirror + Media post-process plumbing. Identity state is a
/// no-op; non-identity state transforms a known RGB pixel predictably; and the
/// Update endpoints round-trip through IConfigStore.
/// </summary>
public class LightingPostProcessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;

    public LightingPostProcessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-pp-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void Identity_LeavesPixelsUnchanged()
    {
        var pp = new PostProcessState();
        Assert.True(pp.IsIdentity());
        var pixels = new byte[] { 200, 100, 50, 12, 34, 56 };
        var snapshot = (byte[])pixels.Clone();
        RgbPostProcess.Apply(pixels, pp);
        Assert.Equal(snapshot, pixels);
    }

    [Fact]
    public void NonIdentity_TransformsPixel()
    {
        // Saturation=0 collapses every channel to the luma of the pixel so we
        // get predictable grayscale output. The tonemap still squashes high
        // values, so we compare against the expected formula (luma -> tonemap).
        var pp = new PostProcessState { Saturation = 0f };
        var pixels = new byte[] { 200, 100, 50 };
        RgbPostProcess.Apply(pixels, pp);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
    }

    [Fact]
    public void UpdateScreenEffect_PersistsWhenPersistTrue()
    {
        _provider.UpdateScreenEffect(0.25f, 0.5f, 1.2f, 1.1f, flipX: false, flipY: false, persist: true);
        _store.FlushNow();

        var s = _store.Load().Lighting.ScreenEffect;
        Assert.Equal(0.25f, s.Hue);
        Assert.Equal(0.5f, s.Colorize);
        Assert.Equal(1.2f, s.Saturation);
        Assert.Equal(1.1f, s.Contrast);
        Assert.False(s.FlipX);
        Assert.False(s.FlipY);
    }

    [Fact]
    public void UpdateScreenEffect_SkipsPersistWhenPersistFalse()
    {
        // Prime disk with a baseline so we can detect a subsequent write.
        _provider.UpdateScreenEffect(0.1f, 0.1f, 1f, 1f, flipX: false, flipY: false, persist: true);
        _store.FlushNow();
        var baselineMtime = File.GetLastWriteTimeUtc(_settingsPath);
        var baselineLen = new FileInfo(_settingsPath).Length;

        // Live-drag updates: should update in-memory state but NOT touch disk.
        for (int i = 0; i < 30; i++)
        {
            _provider.UpdateScreenEffect(i / 30f, 0.5f, 1f, 1f, flipX: false, flipY: false, persist: false);
        }
        _store.FlushNow();

        Assert.Equal(baselineMtime, File.GetLastWriteTimeUtc(_settingsPath));
        Assert.Equal(baselineLen, new FileInfo(_settingsPath).Length);
    }

    [Fact]
    public void UpdateMediaEffect_PersistsIndependentlyFromScreen()
    {
        _provider.UpdateScreenEffect(0.1f, 0.1f, 1f, 1f, flipX: false, flipY: false, persist: true);
        _provider.UpdateMediaEffect(0.9f, 0.8f, 2f, 1.5f, flipX: false, flipY: false, persist: true);
        _store.FlushNow();

        var s = _store.Load().Lighting;
        Assert.Equal(0.1f, s.ScreenEffect.Hue);
        Assert.Equal(0.9f, s.MediaEffect.Hue);
        Assert.Equal(0.8f, s.MediaEffect.Colorize);
        Assert.Equal(2f, s.MediaEffect.Saturation);
        Assert.Equal(1.5f, s.MediaEffect.Contrast);
    }

    [Fact]
    public void UpdateScreenEffect_PersistsFlipFlags()
    {
        _provider.UpdateScreenEffect(0f, 0f, 1f, 1f, flipX: true, flipY: true, persist: true);
        _store.FlushNow();

        var s = _store.Load().Lighting.ScreenEffect;
        Assert.True(s.FlipX);
        Assert.True(s.FlipY);
    }

    [Fact]
    public void DefaultsAreIdentity()
    {
        var s = _store.Load().Lighting;
        Assert.Equal(0f, s.ScreenEffect.Hue);
        Assert.Equal(0f, s.ScreenEffect.Colorize);
        Assert.Equal(1f, s.ScreenEffect.Saturation);
        Assert.Equal(1f, s.ScreenEffect.Contrast);
        Assert.False(s.ScreenEffect.FlipX);
        Assert.False(s.ScreenEffect.FlipY);
        Assert.Equal(0f, s.MediaEffect.Hue);
        Assert.Equal(0f, s.MediaEffect.Colorize);
        Assert.Equal(1f, s.MediaEffect.Saturation);
        Assert.Equal(1f, s.MediaEffect.Contrast);
        Assert.False(s.MediaEffect.FlipX);
        Assert.False(s.MediaEffect.FlipY);
    }

    [Fact]
    public void CanvasBuffer_FlipX_MirrorsRows()
    {
        var canvas = new CanvasBuffer(4, 2);
        canvas.SetPixel(0, 0, 10, 20, 30);
        canvas.SetPixel(3, 0, 40, 50, 60);
        canvas.SetPixel(0, 1, 70, 80, 90);
        canvas.SetPixel(3, 1, 100, 110, 120);

        canvas.ApplyFlip(flipX: true, flipY: false);

        Assert.Equal(((byte)40, (byte)50, (byte)60), canvas.GetPixel(0, 0));
        Assert.Equal(((byte)10, (byte)20, (byte)30), canvas.GetPixel(3, 0));
        Assert.Equal(((byte)100, (byte)110, (byte)120), canvas.GetPixel(0, 1));
        Assert.Equal(((byte)70, (byte)80, (byte)90), canvas.GetPixel(3, 1));
    }

    [Fact]
    public void CanvasBuffer_FlipY_MirrorsColumns()
    {
        var canvas = new CanvasBuffer(2, 4);
        canvas.SetPixel(0, 0, 10, 20, 30);
        canvas.SetPixel(0, 3, 40, 50, 60);

        canvas.ApplyFlip(flipX: false, flipY: true);

        Assert.Equal(((byte)40, (byte)50, (byte)60), canvas.GetPixel(0, 0));
        Assert.Equal(((byte)10, (byte)20, (byte)30), canvas.GetPixel(0, 3));
    }
}
