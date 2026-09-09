using System;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Real ffmpeg bakes, not mocks: the whole transparency feature rests on the
/// bundled binary having a png/gif encoder and the palettegen pair, and a
/// rebuild that drops those configure flags produces a service that still
/// compiles, still imports, and silently flattens every transparent background.
/// Nothing below the ffmpeg process boundary can catch that, so these assert on
/// the bytes ffmpeg actually wrote.
///
/// Fixtures are literal base64 (authored with PIL, decoded here): the bundled
/// build has no lavfi, so it cannot generate its own test media.
///
/// The baking tests carry <see cref="FfmpegFactAttribute"/> so a host with no
/// ffmpeg reports them skipped instead of failing the class. A publish without
/// the bundled binary is already a hard MSBuild error, so nothing is lost.
/// </summary>
public sealed class PanelBgTransparencyTests : IDisposable
{
    // 8x8 RGBA png: left half opaque red, right half transparent - and the
    // transparent pixels carry RGB (255,0,255), so "alpha was dropped" and
    // "alpha was matted to black" are distinguishable outcomes.
    private const string TransparentPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAHUlEQVR4nGO8o6HxnwEJKN+4zojMZ0LmYAPDQwEAAEgEDtnh8EMAAAAASUVORK5CYII=";

    // 2-frame 8x8 gif: a green block on the left half of frame 0, transparent
    // background - so the right half of frame 0 is the transparency under test.
    private const string TransparentGifBase64 =
        "R0lGODlhCAAIAIEAAP8A/yjIUAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAACAAIAAAIGwADCAwAoCCAgQQNIjR4cCDDhQodRhT4UGLBgAAh+QQJCgAAACwEAAAABAAIAIH/AP8oyFAAAAAAAAAIDAADCBxIsKDBgwQDAgA7";

    private const string DeviceId = "test-device";
    private static readonly CropRect FullFrame = new(0, 0, 1, 1);

    private readonly string _root;
    private readonly PanelBgLibrary _library;

    public PanelBgTransparencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-bg-alpha-" + Guid.NewGuid().ToString("N"));
        _library = new PanelBgLibrary(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    /// <summary>Stages a fixture and returns its stageId.</summary>
    private async Task<string> StageAsync(string base64, string name)
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        File.WriteAllBytes(temp, Convert.FromBase64String(base64));
        var staged = await PanelBgImporter.StageAsync(_library, DeviceId, temp, name);
        Assert.True(staged.Ok, staged.Error);
        return staged.StageId!;
    }

    /// <summary>
    /// Decodes an asset's first frame and reads the top-right pixel - the half of
    /// both fixtures that is transparent. Reads the whole raster rather than
    /// cropping to 1x1: a 1x1 crop of a yuv420p frame is an invalid chroma size
    /// and ffmpeg refuses it.
    /// </summary>
    private static async Task<(byte R, byte G, byte B, byte A)> TopRightPixelAsync(string path, int width)
    {
        var raw = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            await MediaImporter.RunFfmpeg("-y", "-i", path, "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "rgba", raw);
            var bytes = File.ReadAllBytes(raw);
            var offset = (width - 1) * 4;
            Assert.True(bytes.Length >= offset + 4, "decoded frame is empty");
            return (bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]);
        }
        finally
        {
            try { File.Delete(raw); }
            catch { }
        }
    }

    [FfmpegFact]
    public async Task TransparentPngKeepsItsAlpha()
    {
        var stageId = await StageAsync(TransparentPngBase64, "circle.png");
        var result = await PanelBgImporter.CommitAsync(_library, DeviceId, stageId, FullFrame, 16, 16);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Item!.Alpha);
        Assert.True(File.Exists(_library.GetMediaPath(DeviceId, result.Item.Id, ".png")));
        Assert.True(File.Exists(_library.GetThumbPath(DeviceId, result.Item.Id, alpha: true)));

        var corner = await TopRightPixelAsync(_library.GetMediaPath(DeviceId, result.Item.Id, ".png"), 16);
        Assert.Equal(0, corner.A);
    }

    [FfmpegFact]
    public async Task TransparentPngMattesToBlackWhenTransparencyIsOff()
    {
        var stageId = await StageAsync(TransparentPngBase64, "circle.png");
        var result = await PanelBgImporter.CommitAsync(
            _library, DeviceId, stageId, FullFrame, 16, 16, keepTransparency: false);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.Item!.Alpha);
        var media = _library.GetMediaPath(DeviceId, result.Item.Id, ".jpg");
        Assert.True(File.Exists(media));

        // Black, not the (255,0,255) sitting under the alpha - which is what an
        // unmatted "drop the alpha channel" encode emits.
        var corner = await TopRightPixelAsync(media, 16);
        Assert.True(corner.R < 24 && corner.G < 24 && corner.B < 24,
            $"expected a black matte, got ({corner.R},{corner.G},{corner.B})");
    }

    [FfmpegFact]
    public async Task TransparentGifStaysAnimatedAndTransparent()
    {
        var stageId = await StageAsync(TransparentGifBase64, "moving.gif");
        var result = await PanelBgImporter.CommitAsync(_library, DeviceId, stageId, FullFrame, 16, 16);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Item!.Alpha);
        Assert.Equal("animated", result.Item.Type);
        Assert.True(File.Exists(_library.GetMediaPath(DeviceId, result.Item.Id, ".gif")));

        var corner = await TopRightPixelAsync(_library.GetMediaPath(DeviceId, result.Item.Id, ".gif"), 16);
        Assert.Equal(0, corner.A);
    }

    [FfmpegFact]
    public async Task TransparentGifBecomesAnOpaqueVideoWhenTransparencyIsOff()
    {
        var stageId = await StageAsync(TransparentGifBase64, "moving.gif");
        var result = await PanelBgImporter.CommitAsync(
            _library, DeviceId, stageId, FullFrame, 16, 16, keepTransparency: false);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.Item!.Alpha);
        Assert.True(File.Exists(_library.GetMediaPath(DeviceId, result.Item.Id, ".mp4")));

        var corner = await TopRightPixelAsync(_library.GetMediaPath(DeviceId, result.Item.Id, ".mp4"), 16);
        Assert.True(corner.R < 24 && corner.G < 24 && corner.B < 24,
            $"expected a black matte, got ({corner.R},{corner.G},{corner.B})");
    }

    [FfmpegFact]
    public async Task OpaqueSourceKeepsTheJpegPipeline()
    {
        // The same png with its alpha filled in: nothing to keep, so a ticked
        // box must not push an ordinary photo onto the lossless png path.
        var opaque = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(source, Convert.FromBase64String(TransparentPngBase64));
        await MediaImporter.RunFfmpeg("-y", "-i", source, "-frames:v", "1", "-q:v", "3", opaque);
        File.Delete(source);

        var staged = await PanelBgImporter.StageAsync(_library, DeviceId, opaque, "photo.jpg");
        Assert.True(staged.Ok, staged.Error);
        var result = await PanelBgImporter.CommitAsync(_library, DeviceId, staged.StageId!, FullFrame, 16, 16);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.Item!.Alpha);
        Assert.True(File.Exists(_library.GetMediaPath(DeviceId, result.Item.Id, ".jpg")));
    }

    [FfmpegFact]
    public async Task StagePreviewIsPngOnlyForATransparentSource()
    {
        var alphaStage = await StageAsync(TransparentPngBase64, "circle.png");
        Assert.EndsWith(".preview.png", _library.FindStagedPreview(DeviceId, alphaStage));

        var opaque = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(source, Convert.FromBase64String(TransparentPngBase64));
        await MediaImporter.RunFfmpeg("-y", "-i", source, "-frames:v", "1", "-q:v", "3", opaque);
        File.Delete(source);

        var opaqueStage = await PanelBgImporter.StageAsync(_library, DeviceId, opaque, "photo.jpg");
        Assert.True(opaqueStage.Ok, opaqueStage.Error);
        Assert.EndsWith(".preview.jpg", _library.FindStagedPreview(DeviceId, opaqueStage.StageId!));
    }

    [Theory]
    [InlineData(1280, 720, 480, 270)]
    [InlineData(720, 1280, 270, 480)]
    [InlineData(480, 480, 480, 480)]
    // Smaller than the box: force_original_aspect_ratio=decrease never upscaled,
    // and neither does the size we now compute for the matte canvas.
    [InlineData(320, 240, 320, 240)]
    public void ThumbSizeBoxesWithoutUpscaling(int w, int h, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), PanelBgImporter.ThumbSize(w, h));
    }
}
