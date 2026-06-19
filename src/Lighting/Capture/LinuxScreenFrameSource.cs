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
/// (the same Nexus binary re-invoked as the session user via setpriv) which does
/// the xdg-desktop-portal ScreenCast handshake and runs a gst-launch pipewiresrc
/// consumer; the helper writes raw RGB24 frames to its stdout, which we read into
/// a double-buffered latest-frame the effect samples.
///
/// The capture is kept as a single stable session: the engine re-applies the
/// lighting effect on every RGB re-sync, which would otherwise tear down and
/// respawn a fresh portal session each time. <see cref="Start"/> reuses a healthy
/// helper of the same size, and <see cref="Stop"/> defers the kill briefly so a
/// Stop()-then-Start() churn keeps the same capture. <see cref="Reselect"/> drops
/// the saved permission token and the current helper so the next start re-opens
/// the system screen picker (the only way to change the mirrored screen on Wayland).
///
/// Compiles on every platform; only constructed/bound on Linux.
/// </summary>
public sealed class LinuxScreenFrameSource : IScreenFrameSource
{
    private readonly object _frameLock = new();   // guards the frame buffer (hot path)
    private readonly object _lifeLock = new();     // guards the helper lifecycle
    private byte[]? _latest;
    private int _w, _h;            // latest frame dimensions (frameLock)
    private int _reqW, _reqH;      // requested capture size (lifeLock)
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private int _stopGen; // bumped to cancel a pending deferred Stop

    public void Start(string monitorId, int width, int height)
    {
        lock (_lifeLock)
        {
            _stopGen++; // a (re-)apply: cancel any pending deferred stop
            if (_proc is not null && !_proc.HasExited && _reqW == width && _reqH == height)
                return; // healthy capture at this size already running - reuse it
            KillLocked();
            _reqW = width;
            _reqH = height;
            SpawnLocked(width, height);
        }
    }

    private void SpawnLocked(int width, int height)
    {
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
        _proc = proc;
        _cts = cts;
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
        int gen;
        lock (_lifeLock)
        {
            if (_proc is null)
                return;
            gen = ++_stopGen; // any later Start()/Stop() supersedes this one
        }
        // Defer the kill: a Stop() immediately followed by Start() (the effect
        // re-created on an RGB re-sync) keeps the same capture, not a new portal
        // session. An actual mode switch leaves no Start() to cancel it, so the
        // deferred kill fires.
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            lock (_lifeLock)
            {
                if (gen != _stopGen)
                    return;
                KillLocked();
            }
            lock (_frameLock)
            {
                _latest = null;
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Forget the saved screen choice and drop the current capture so the next
    /// start re-opens the system screen picker - the only way to change which
    /// screen is mirrored on Wayland.
    /// </summary>
    public void Reselect()
    {
        LinuxScreenCastHelper.DeleteRestoreToken();
        lock (_lifeLock)
        {
            _stopGen++;
            KillLocked();
        }
        lock (_frameLock)
        {
            _latest = null;
        }
    }

    // Kill the current helper (and its gst child) immediately. Hold _lifeLock.
    private void KillLocked()
    {
        var proc = _proc;
        var cts = _cts;
        _proc = null;
        _cts = null;
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
