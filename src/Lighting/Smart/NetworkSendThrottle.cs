using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Per-device, latest-wins send loop with an optional shared per-host rate gate.
///
/// Submit() never blocks the caller (the 30 Hz engine tick or a control route):
/// it stores the latest frame and a dedicated background loop sends it via the
/// device's driver, then waits the device's min interval before sending the
/// next latest frame. Bursts collapse to the most recent state.
///
/// The per-host gate caps TOTAL sends to a shared controller (e.g. a Hue bridge
/// driving many bulbs) - without it, N lights each at 10/s would flood a bridge
/// that only handles ~10/s total, saturating it until it stops responding. With
/// the gate, the bridge stays healthy and each light simply refreshes less often.
/// </summary>
public sealed class NetworkSendThrottle : IDisposable
{
    private sealed class Channel
    {
        public required System.Threading.Channels.Channel<LightFrame> Ch { get; init; }
        public required Task Loop { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    private sealed class HostGate
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public long LastTicks;
    }

    private readonly ConcurrentDictionary<string, Channel> _channels = new();
    private readonly ConcurrentDictionary<string, HostGate> _hostGates = new();
    private bool _disposed;

    /// <summary>Queue the latest desired frame for a device. <paramref name="send"/>
    /// is the brand-specific push (driver + device captured by the caller).
    /// When <paramref name="hostKey"/> is set, sends to the same host are spaced
    /// at least <paramref name="hostIntervalMs"/> apart across all devices that
    /// share it (the controller-level rate cap).</summary>
    public void Submit(
        string id,
        LightFrame frame,
        int minIntervalMs,
        Func<LightFrame, CancellationToken, Task> send,
        string? hostKey = null,
        int hostIntervalMs = 0)
    {
        if (_disposed) return;
        var chan = _channels.GetOrAdd(id, key =>
            CreateChannel(key, Math.Max(1, minIntervalMs), send, hostKey, hostIntervalMs));
        // Capacity-1 DropOldest: a new frame replaces an unsent one (latest wins).
        chan.Ch.Writer.TryWrite(frame);
    }

    /// <summary>Stop and forget a device's loop (on unpair / disable).</summary>
    public void Remove(string id)
    {
        if (_channels.TryRemove(id, out var chan))
        {
            chan.Ch.Writer.TryComplete();
            chan.Cts.Cancel();
        }
    }

    private Channel CreateChannel(
        string id, int minIntervalMs, Func<LightFrame, CancellationToken, Task> send,
        string? hostKey, int hostIntervalMs)
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<LightFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => RunAsync(id, ch.Reader, minIntervalMs, send, hostKey, hostIntervalMs, cts.Token));
        return new Channel { Ch = ch, Loop = loop, Cts = cts };
    }

    private async Task RunAsync(
        string id,
        ChannelReader<LightFrame> reader,
        int minIntervalMs,
        Func<LightFrame, CancellationToken, Task> send,
        string? hostKey,
        int hostIntervalMs,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (hostKey is not null && hostIntervalMs > 0)
                {
                    try { await WaitHostTurnAsync(hostKey, hostIntervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                try { await send(frame, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // A single failed send must not kill the loop - the device may
                    // come back. Online status is probed separately, not inferred here.
                    ServiceLog.Warn($"[smart-lights] send failed for {id}: {ex.GetType().Name}: {ex.Message}");
                }
                try { await Task.Delay(minIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    /// <summary>Block until at least <paramref name="intervalMs"/> has elapsed
    /// since the last grant for this host, then record now as the new grant time.
    /// Serializes only the (cheap) scheduling decision - the send itself runs
    /// outside the lock - so total grants/s to a host ≤ 1000/intervalMs.</summary>
    private async Task WaitHostTurnAsync(string hostKey, int intervalMs, CancellationToken ct)
    {
        var gate = _hostGates.GetOrAdd(hostKey, _ => new HostGate());
        await gate.Sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = gate.LastTicks + intervalMs - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            gate.LastTicks = Environment.TickCount64;
        }
        finally { gate.Sem.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var chan in _channels.Values)
        {
            chan.Ch.Writer.TryComplete();
            chan.Cts.Cancel();
        }
        _channels.Clear();
    }
}
