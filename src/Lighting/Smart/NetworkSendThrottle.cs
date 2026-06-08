using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Per-device, latest-wins send loop. Submit() never blocks the caller (the
/// 30 Hz engine tick or a control route): it stores the latest frame and a
/// dedicated background loop sends it via the device's driver, then waits the
/// device's min interval before sending the next latest frame. Bursts collapse
/// to the most recent state, so a slow bridge can't stall the engine and a
/// chatty effect can't exceed a device's rate ceiling.
/// </summary>
public sealed class NetworkSendThrottle : IDisposable
{
    private sealed class Channel
    {
        public required System.Threading.Channels.Channel<LightFrame> Ch { get; init; }
        public required Task Loop { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    private readonly ConcurrentDictionary<string, Channel> _channels = new();
    private bool _disposed;

    /// <summary>Queue the latest desired frame for a device. <paramref name="send"/>
    /// is the brand-specific push (driver + device captured by the caller).</summary>
    public void Submit(string id, LightFrame frame, int minIntervalMs, Func<LightFrame, CancellationToken, Task> send)
    {
        if (_disposed) return;
        var chan = _channels.GetOrAdd(id, key => CreateChannel(key, Math.Max(1, minIntervalMs), send));
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

    private Channel CreateChannel(string id, int minIntervalMs, Func<LightFrame, CancellationToken, Task> send)
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<LightFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => RunAsync(id, ch.Reader, minIntervalMs, send, cts.Token));
        return new Channel { Ch = ch, Loop = loop, Cts = cts };
    }

    private static async Task RunAsync(
        string id,
        ChannelReader<LightFrame> reader,
        int minIntervalMs,
        Func<LightFrame, CancellationToken, Task> send,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await send(frame, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // A single failed send must not kill the loop — the device may
                    // come back. Log sparsely; the provider flips Online on failure.
                    ServiceLog.Warn($"[smart-lights] send failed for {id}: {ex.GetType().Name}: {ex.Message}");
                }
                try { await Task.Delay(minIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
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
