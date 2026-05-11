using System;
using System.IO;

namespace Qos.Service.Lighting.Engine.Effects;

/// <summary>
/// Renders a media item imported through MediaLibrary: a raw RGB24 byte
/// sequence stored at {itemDir}/frames.bin, with per-frame size known from
/// the item's Width/Height and playback rate from its Fps. Static items
/// (1 frame) display the single frame forever; animated items advance based
/// on wall-clock deltas so playback speed is independent of engine fps.
/// </summary>
public sealed class MediaFramesEffect : IEffect
{
    public string Name => "media";
    private readonly byte[] _frames;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly int _frameCount;
    private readonly double _frameDurationMs;
    private readonly PostProcessState _postProcess;
    private int _currentFrame;
    private double _lastAdvanceMs;
    private bool _started;

    public MediaFramesEffect(string framesBinPath, int width, int height, int fps, PostProcessState? postProcess = null)
    {
        _frameWidth = Math.Max(1, width);
        _frameHeight = Math.Max(1, height);
        _frames = File.Exists(framesBinPath) ? File.ReadAllBytes(framesBinPath) : Array.Empty<byte>();
        var perFrame = _frameWidth * _frameHeight * 3;
        _frameCount = perFrame > 0 ? _frames.Length / perFrame : 0;
        _frameDurationMs = fps > 0 ? 1000.0 / fps : 0;
        _postProcess = postProcess ?? new PostProcessState();
    }

    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        if (_frameCount == 0)
        { canvas.Fill(30, 30, 40); return; }

        // Anchor the clock on the first frame. The engine passes wall-clock
        // Unix ms as tickMs, so without this anchor the initial delta would
        // be ~1.8 trillion and the (int) cast on `steps` overflows, yielding
        // a garbage start frame that makes the first loop look like it plays
        // from the middle (user-reported as "plays in reverse the first second").
        if (!_started)
        {
            _lastAdvanceMs = tickMs;
            _started = true;
        }

        if (_frameCount > 1 && _frameDurationMs > 0 && tickMs - _lastAdvanceMs >= _frameDurationMs)
        {
            // Clamp to one full loop per render call. Handles clock jumps
            // (suspend/resume) without skipping the whole sequence in one tick.
            var steps = Math.Min(_frameCount, (int)((tickMs - _lastAdvanceMs) / _frameDurationMs));
            _currentFrame = (_currentFrame + steps) % _frameCount;
            _lastAdvanceMs += steps * _frameDurationMs;
        }

        var perFrame = _frameWidth * _frameHeight * 3;
        var offset = _currentFrame * perFrame;
        if (_frameWidth == canvas.Width && _frameHeight == canvas.Height)
        {
            canvas.WriteFromRgb(new ReadOnlySpan<byte>(_frames, offset, perFrame));
            canvas.ApplyPostProcess(_postProcess);
            return;
        }

        for (int y = 0; y < canvas.Height; y++)
        {
            var sy = y * _frameHeight / canvas.Height;
            sy = Math.Clamp(sy, 0, _frameHeight - 1);
            for (int x = 0; x < canvas.Width; x++)
            {
                var sx = x * _frameWidth / canvas.Width;
                sx = Math.Clamp(sx, 0, _frameWidth - 1);
                var srcOff = offset + (sy * _frameWidth + sx) * 3;
                canvas.SetPixel(x, y, _frames[srcOff], _frames[srcOff + 1], _frames[srcOff + 2]);
            }
        }
        canvas.ApplyPostProcess(_postProcess);
    }

    public void Dispose() { }
}
