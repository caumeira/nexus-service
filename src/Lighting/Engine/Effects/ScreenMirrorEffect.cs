using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Lighting.Capture;
using Qos.Service.Platform;

namespace Qos.Service.Lighting.Engine.Effects;

public sealed class ScreenMirrorEffect : IEffect
{
    public string Name => "screen";
    private readonly string _monitorId;
    private readonly PostProcessState _postProcess;
    private readonly IScreenFrameSource? _frameSource;
    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private int _captureW, _captureH;
    private bool _started;
    private bool _hasFrame;
    private int _frameCount, _acquireHits;
    private long _lastLogTick;
    private CancellationTokenSource? _cts;
    private Process? _proc;
    private Task? _readerTask;

    public ScreenMirrorEffect(string monitorId = "", PostProcessState? postProcess = null, IScreenFrameSource? frameSource = null)
    {
        _monitorId = monitorId;
        _postProcess = postProcess ?? new PostProcessState();
        _frameSource = frameSource;
    }
#if WINDOWS
    private Capture.DxgiScreenCapture? _dxgi;
#endif

    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        if (!_started)
        {
            EnsureStarted(canvas.Width, canvas.Height);
        }
        if (_frameSource is not null)
        {
            var helperFrame = _frameSource.TryAcquireFrame(out var helperW, out var helperH);
            if (helperFrame is null || helperW <= 0 || helperH <= 0)
            {
                canvas.Fill(20, 20, 24);
                return;
            }
            BlitToCanvas(canvas, helperFrame, helperW, helperH);
            canvas.ApplyFlip(_postProcess.FlipX, _postProcess.FlipY);
            canvas.ApplyPostProcess(_postProcess);
            return;
        }
#if WINDOWS
        if (_dxgi is not null && _dxgi.IsInitialized)
        {
            bool freshFrame = false;
            if (_dxgi.AcquireFrame())
            {
                if (_dxgi.BlitToCanvas(canvas)) { _hasFrame = true; _acquireHits++; freshFrame = true; }
                _dxgi.ReleaseFrame();
            }
            _frameCount++;
            var now = Environment.TickCount64;
            if (now - _lastLogTick > 5000)
            {
                var elapsed = now - _lastLogTick;
                Console.Error.WriteLine($"[screen-mirror] {_acquireHits}/{_frameCount} new ({_acquireHits * 100 / Math.Max(1, _frameCount)}%), {1000.0 * _frameCount / Math.Max(1, elapsed):F1} fps");
                _frameCount = 0; _acquireHits = 0; _lastLogTick = now;
            }
            if (_hasFrame)
            {
                // DXGI only hands us a fresh frame when something on screen
                // actually changed. On cached ticks the canvas still holds the
                // post-processed output from the last blit - re-running
                // ApplyPostProcess (or ApplyFlip) here would compound the
                // transform each frame. Only re-apply when we got new pixels.
                if (freshFrame)
                {
                    canvas.ApplyFlip(_postProcess.FlipX, _postProcess.FlipY);
                    canvas.ApplyPostProcess(_postProcess);
                }
                return;
            }
            canvas.Fill(20, 20, 24);
            return;
        }
#endif
        byte[]? frame;
        int fw, fh;
        lock (_frameLock)
        { frame = _latestFrame; fw = _captureW; fh = _captureH; }
        if (frame is null)
        { canvas.Fill(20, 20, 24); return; }
        BlitToCanvas(canvas, frame, fw, fh);
        canvas.ApplyFlip(_postProcess.FlipX, _postProcess.FlipY);
        canvas.ApplyPostProcess(_postProcess);
    }

    private static void BlitToCanvas(CanvasBuffer canvas, byte[] src, int srcW, int srcH)
    {
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                var sx = Math.Clamp(x * srcW / canvas.Width, 0, srcW - 1);
                var sy = Math.Clamp(y * srcH / canvas.Height, 0, srcH - 1);
                var off = (sy * srcW + sx) * 3;
                if (off + 2 < src.Length)
                {
                    canvas.SetPixel(x, y, src[off], src[off + 1], src[off + 2]);
                }
            }
        }
    }

    private void EnsureStarted(int w, int h)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _captureW = w;
        _captureH = h;
        if (_frameSource is not null)
        {
            try { _frameSource.Start(_monitorId, w, h); Console.Error.WriteLine("[screen-mirror] using helper frame source"); }
            catch (Exception ex) { Console.Error.WriteLine($"[screen-mirror] helper frame source start failed: {ex.Message}"); }
            return;
        }
#if WINDOWS
        var outputIdx = uint.TryParse(_monitorId, out var idx) ? idx : 0u;
        _dxgi = new Capture.DxgiScreenCapture();
        if (_dxgi.Initialize(outputIdx)) { Console.Error.WriteLine("[screen-mirror] using DXGI"); return; }
        _dxgi.Dispose(); _dxgi = null;
#endif
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        { StartFfmpeg(w, h); }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-mirror] ffmpeg failed: {ex.Message}"); }
    }

    private void StartFfmpeg(int w, int h)
    {
        _cts = new CancellationTokenSource();
        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var ffmpegPath = Qos.Service.Platform.FfmpegResolver.Path
            ?? throw new InvalidOperationException("ffmpeg not found");
        var psi = new ProcessStartInfo { FileName = ffmpegPath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (isWin)
        { psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("gdigrab"); psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add("30"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("desktop"); }
        else
        { var devIdx = string.IsNullOrEmpty(_monitorId) ? "1" : _monitorId; psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("avfoundation"); psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add("30"); psi.ArgumentList.Add("-capture_cursor"); psi.ArgumentList.Add("0"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(devIdx); }
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={w}:{h}:flags=neighbor");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgb24");
        psi.ArgumentList.Add("-");
        _proc = Process.Start(psi);
        if (_proc is null)
        {
            throw new InvalidOperationException("null");
        }

        FfmpegTracker.Track(_proc.Id);
        _ = Task.Run(async () => { try { while (!_cts!.IsCancellationRequested && !_proc.HasExited) { if (await _proc.StandardError.ReadLineAsync() is null) { break; } } } catch { } });
        _readerTask = Task.Run(() => ReaderLoopAsync(w, h, _cts.Token));
    }

    private async Task ReaderLoopAsync(int w, int h, CancellationToken ct)
    {
        if (_proc is null)
        {
            return;
        }

        var frameBytes = w * h * 3;
        var buffer = new byte[frameBytes];
        var snapA = new byte[frameBytes];
        var snapB = new byte[frameBytes];
        var useA = true;
        var stream = _proc.StandardOutput.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < frameBytes)
                { var n = await stream.ReadAsync(buffer.AsMemory(read, frameBytes - read), ct); if (n <= 0) { return; } read += n; }
                var snap = useA ? snapA : snapB;
                Buffer.BlockCopy(buffer, 0, snap, 0, frameBytes);
                lock (_frameLock)
                { _latestFrame = snap; _captureW = w; _captureH = h; }
                useA = !useA;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-mirror] reader crashed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_frameSource is not null)
        {
            try { _frameSource.Stop(); } catch { }
        }
#if WINDOWS
        _dxgi?.Dispose();
#endif
        try
        { _cts?.Cancel(); }
        catch { }
        try
        {
            if (_proc is not null && !_proc.HasExited)
            { FfmpegTracker.Untrack(_proc.Id); _proc.Kill(entireProcessTree: true); _proc.WaitForExit(1000); }
            else if (_proc is not null)
            {
                FfmpegTracker.Untrack(_proc.Id);
            }
        }
        catch { }
        try
        { _proc?.Dispose(); }
        catch { }
        try
        { _cts?.Dispose(); }
        catch { }
        _proc = null;
        _cts = null;
        _latestFrame = null;
        _started = false;
        _hasFrame = false;
        _frameCount = 0;
        _acquireHits = 0;
    }
}
