using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Dedicated per-session thread that clocks frames onto the device transport
/// at the display rate. The device player renders on arrival, so the wire IS
/// the display clock: encoder bursts sent as-is show as speed wobble, and any
/// mid-GOP gap smears until the next IDR. Pacing decisions live in
/// <see cref="PacingPolicy"/>; this class only keeps time and writes.
/// </summary>
public sealed class PacedStreamWriter : IDisposable
{
    private readonly StreamSession _session;
    private readonly int _fps;
    private readonly Action<IStreamedPanelTransport, Exception> _onTransportFault;
    private readonly object _transportLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private IStreamedPanelTransport? _transport;
    private long _lastWriteStallLogTicks;
    private long _lastStatsLogTicks;
    private (long Enqueued, long Sent, long Dropped, long Trims, int Depth, long MaxIngestGapMs) _lastStats;
    private volatile bool _disposed;

    public PacedStreamWriter(StreamSession session, Action<IStreamedPanelTransport, Exception> onTransportFault)
    {
        _session = session;
        _fps = Math.Clamp(session.Info.Profile.Fps, 1, 240);
        _onTransportFault = onTransportFault;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"stream-writer-{session.Info.Serial}",
        };
        _thread.Start();
    }

    /// <summary>Swap in a (re)opened transport; arms the session's IDR resync latch.</summary>
    public void SetTransport(IStreamedPanelTransport transport)
    {
        lock (_transportLock) _transport = transport;
        _session.RequireIdrResync();
        _wake.Set();
    }

    public void ClearTransport()
    {
        lock (_transportLock) _transport = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
            _thread.Join(2000);
        _wake.Dispose();
    }

    private void Run()
    {
        // Windows rounds Thread.Sleep to the ~15ms scheduler quantum by
        // default, which jitters the tick visibly; request finer timer
        // resolution while this thread lives.
        if (OperatingSystem.IsWindows()) TimeBeginPeriod(1);
        try
        {
            var interval = Stopwatch.Frequency / _fps;
            var deadline = Stopwatch.GetTimestamp() + interval;
            while (!_disposed)
            {
                IStreamedPanelTransport? transport;
                lock (_transportLock) transport = _transport;
                if (transport is null || !transport.IsOpen)
                {
                    _wake.WaitOne(100);
                    deadline = Stopwatch.GetTimestamp() + interval;
                    continue;
                }

                WaitUntil(deadline);
                var now = Stopwatch.GetTimestamp();
                if (_lastStatsLogTicks == 0) _lastStatsLogTicks = now;
                if (now - _lastStatsLogTicks > Stopwatch.Frequency * 30)
                {
                    var stats = _session.StatsSnapshot();
                    var secs = (now - _lastStatsLogTicks) / (double)Stopwatch.Frequency;
                    ServiceLog.Info(
                        $"[streamed-panel] stats serial={_session.Info.Serial} in={(stats.Enqueued - _lastStats.Enqueued) / secs:F1}fps " +
                        $"out={(stats.Sent - _lastStats.Sent) / secs:F1}fps depth={stats.Depth} " +
                        $"dropped={stats.Dropped - _lastStats.Dropped} trims={stats.Trims - _lastStats.Trims} " +
                        $"maxInGap={stats.MaxIngestGapMs}ms");
                    _lastStats = stats;
                    _lastStatsLogTicks = now;
                }
                // A late tick (blocked write, empty stretch) must not bank
                // debt that later bursts the wire; re-anchor instead. What
                // the re-anchor leaves queued drains through the pacing
                // policy's bounded catch-up rather than a same-tick flush.
                if (now - deadline > Stopwatch.Frequency / 10) deadline = now;
                deadline += interval;

                foreach (var frame in _session.DequeueForTick())
                {
                    try
                    {
                        var writeStart = Stopwatch.GetTimestamp();
                        transport.Write(frame.Payload);
                        // A blocked write is downstream backpressure (socket
                        // buffer, adb forwarding, device fifo); on a
                        // render-on-arrival device every stall shows on glass
                        // as a time snap when the backlog flushes.
                        var writeMs = (Stopwatch.GetTimestamp() - writeStart) * 1000 / Stopwatch.Frequency;
                        if (writeMs > 50 && Stopwatch.GetTimestamp() - _lastWriteStallLogTicks > Stopwatch.Frequency)
                        {
                            _lastWriteStallLogTicks = Stopwatch.GetTimestamp();
                            ServiceLog.Warn($"[streamed-panel] write stalled {writeMs}ms serial={_session.Info.Serial} ({frame.Payload.Length}b{(frame.IsIdr ? " idr" : "")})");
                        }
                    }
                    catch (Exception ex)
                    {
                        ClearTransport();
                        _onTransportFault(transport, ex);
                        break;
                    }
                }
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows()) TimeEndPeriod(1);
        }
    }

    private static void WaitUntil(long deadline)
    {
        while (true)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var ms = remaining * 1000 / Stopwatch.Frequency;
            if (ms >= 2) Thread.Sleep((int)ms - 1);
            else if (ms >= 1) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
