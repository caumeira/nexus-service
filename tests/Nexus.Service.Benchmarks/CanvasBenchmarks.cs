using BenchmarkDotNet.Attributes;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// The LED canvas is rewritten every lighting frame (~30 Hz per device). These
/// are the per-frame buffer ops every effect and the GPU-readback path run.
/// </summary>
[MemoryDiagnoser]
[InProcess]
public class CanvasBenchmarks
{
    private readonly CanvasBuffer _canvas = new(160, 90);
    private readonly byte[] _src = new byte[160 * 90 * 3];
    // Non-identity so ApplyPostProcess exercises the real per-pixel path (the
    // screen-mirror / media effects run this every frame).
    private readonly PostProcessState _post = new() { Hue = 0.1f, Colorize = 0.2f, Saturation = 1.2f, Contrast = 1.1f };

    [Benchmark]
    public void Fill() => _canvas.Fill(255, 128, 64);

    [Benchmark]
    public void Clear() => _canvas.Clear();

    [Benchmark]
    public void WriteFromRgb() => _canvas.WriteFromRgb(_src);

    [Benchmark]
    public void SetPixelGrid()
    {
        for (int y = 0; y < _canvas.Height; y++)
        {
            for (int x = 0; x < _canvas.Width; x++)
            {
                _canvas.SetPixel(x, y, (byte)x, (byte)y, 0);
            }
        }
    }

    // Screen-mirror / media per-frame CPU surface (~30 Hz engine tick).
    [Benchmark]
    public void ApplyPostProcess() => _canvas.ApplyPostProcess(_post);

    [Benchmark]
    public void ApplyFlipBoth() => _canvas.ApplyFlip(true, true);
}
