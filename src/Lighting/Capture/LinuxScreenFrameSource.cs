using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Linux screen-mirror frame source. Spawns the <see cref="LinuxScreenCastHelper"/>
/// (the same Nexus binary, re-invoked as the session user via setpriv) which does
/// the xdg-desktop-portal ScreenCast handshake and runs a gst-launch pipewiresrc
/// consumer; the helper writes raw RGB24 frames to its stdout, which we read into
/// a double-buffered latest-frame the effect samples.
///
/// The portal must be driven by a user-owned process (it can't read a root
/// caller's /proc), so the daemon can't call it directly — hence the helper.
/// gstreamer does the PipeWire/GPU lifting (DMA-BUF); the daemon only copies a
/// ~14&#160;KB downscaled frame per tick.
///
/// Compiles on every platform; only constructed/bound on Linux.
/// </summary>
public sealed class LinuxScreenFrameSource : IScreenFrameSource
{
    private readonly object _frameLock = new();
    private byte[]? _latest;
    private int _w, _h;
    private CancellationTokenSource? _cts;
    private Process? _proc;

    public void Start(string monitorId, int width, int height)
    {
        lock (_frameLock)
        {
            if (_proc is not null)
                return;
            _w = width;
            _h = height;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("[screen-mirror] cannot resolve own executable path for capture helper");
            return;
        }

        // Re-invoke ourselves as the session user; the helper does the portal +
        // gst and streams frames to its stdout (which becomes our read pipe).
        var (file, args) = LinuxSession.WrapSpawnAsSessionUser(
            exe, new List<string> { LinuxScreenCastHelper.Verb, width.ToString(), height.ToString() });

        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screen-mirror] failed to start capture helper: {ex.Message}");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_frameLock)
        {
            _proc = proc;
            _cts = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    if (line.Length > 0)
                        Console.Error.WriteLine($"[screen-mirror] {line}");
                }
            }
            catch { }
        });
        _ = Task.Run(() => ReaderLoopAsync(proc, width, height, cts.Token));
        Console.Error.WriteLine($"[screen-mirror] capture helper started ({width}x{height})");
    }

    private async Task ReaderLoopAsync(Process proc, int w, int h, CancellationToken ct)
    {
        var frameBytes = w * h * 3;
        var buffer = new byte[frameBytes];
        var snapA = new byte[frameBytes];
        var snapB = new byte[frameBytes];
        var useA = true;
        var stream = proc.StandardOutput.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < frameBytes)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, frameBytes - read), ct);
                    if (n <= 0)
                        return;
                    read += n;
                }
                var snap = useA ? snapA : snapB;
                Buffer.BlockCopy(buffer, 0, snap, 0, frameBytes);
                lock (_frameLock)
                {
                    _latest = snap;
                    _w = w;
                    _h = h;
                }
                useA = !useA;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[screen-mirror] reader crashed: {ex.Message}"); }
    }

    public byte[]? TryAcquireFrame(out int width, out int height)
    {
        lock (_frameLock)
        {
            width = _w;
            height = _h;
            return _latest;
        }
    }

    public void Stop()
    {
        Process? proc;
        CancellationTokenSource? cts;
        lock (_frameLock)
        {
            proc = _proc;
            cts = _cts;
            _proc = null;
            _cts = null;
            _latest = null;
        }
        try { cts?.Cancel(); } catch { }
        try
        {
            if (proc is not null && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1000);
            }
        }
        catch { }
        try { proc?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
