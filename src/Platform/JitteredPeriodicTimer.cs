using System;
using System.Threading;

namespace Nexus.Service.Platform;

/// <summary>
/// Periodic timer that re-arms with a random offset around the configured
/// interval each tick. Spreads coordinated wake-ups across providers so
/// multiple 2 s timers don't fire on the same scheduler tick.
///
/// Disposal: the underlying <see cref="Timer"/> may have a callback in flight
/// when <see cref="Dispose"/> returns. Callbacks must be safe to run during
/// or after Dispose; this wrapper does not block on in-flight ticks. Owners
/// that touch resources cleared by their own Dispose should guard the
/// callback body against post-Dispose state.
/// </summary>
public sealed class JitteredPeriodicTimer : IDisposable
{
    private readonly Timer _timer;
    private readonly int _periodMs;
    private readonly int _jitterMs;
    private readonly Action _callback;
    private int _disposed;

    public JitteredPeriodicTimer(int periodMs, int jitterMs, Action callback)
    {
        if (periodMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(periodMs));
        }

        _periodMs = periodMs;
        _jitterMs = Math.Clamp(jitterMs, 0, periodMs - 1);
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _timer = new Timer(OnFire, null, NextDelay(initial: true), Timeout.Infinite);
    }

    private void OnFire(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _callback();
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _timer.Change(NextDelay(initial: false), Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // Race with Dispose; safe to drop.
                }
            }
        }
    }

    private int NextDelay(bool initial)
    {
        if (initial)
        {
            // Fire near-immediately on first dispatch, but stagger across
            // providers by up to one jitter window so simultaneous startups
            // don't all wake the thread pool on the same tick.
            return _jitterMs == 0 ? 0 : Random.Shared.Next(0, _jitterMs + 1);
        }

        if (_jitterMs == 0)
        {
            return _periodMs;
        }

        return Random.Shared.Next(_periodMs - _jitterMs, _periodMs + _jitterMs + 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _timer.Dispose();
    }
}
