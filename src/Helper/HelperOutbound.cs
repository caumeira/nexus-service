#if WINDOWS
using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side send-only facade. Each helper-side provider (ScreenTimePoller,
/// MediaPusher, ...) takes an instance of this and calls
/// <see cref="SendAsync{T}"/> to push telemetry to the service. The provider
/// does not see the pipe, the reconnect loop, or the write lock - just a
/// typed JSON send call.
///
/// HelperClientLoop pumps the active pipe in and out of this via
/// <see cref="SetActivePipe"/>. While disconnected, sends are dropped
/// (one-way telemetry is fire-and-forget; transient drops during a service
/// restart are acceptable trade-off for not needing an unbounded buffer).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperOutbound
{
    // Long-lived (process-scope); intentionally never disposed - waiters
    // holding the semaphore must never observe ObjectDisposedException.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _activePipe;

    /// <summary>
    /// Install a fresh pipe for outbound writes. Synchronous swap is fine
    /// because callers use <see cref="ClearActivePipeAsync"/> before
    /// disposing the prior pipe, so no in-flight write ever races with a
    /// dispose.
    /// </summary>
    internal void SetActivePipe(NamedPipeClientStream pipe)
    {
        Volatile.Write(ref _activePipe, pipe);
    }

    /// <summary>
    /// Tear down the current pipe reference. Acquires the write lock so
    /// any in-flight <see cref="SendAsync{T}"/> completes before we hand
    /// control back to the caller (who then disposes the pipe). After
    /// this returns, no new send can latch onto the dying pipe.
    /// </summary>
    internal async Task ClearActivePipeAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { Volatile.Write(ref _activePipe, null); }
        finally { _writeLock.Release(); }
    }

    public bool IsConnected => Volatile.Read(ref _activePipe)?.IsConnected == true;

    public async Task SendAsync<T>(string type, T payload, JsonTypeInfo<T> payloadType, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _activePipe) is null) return;

        var env = new HelperEnvelope
        {
            Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, payloadType),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AppJsonContext.Default.HelperEnvelope);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ClearActivePipeAsync may have swapped us to null while we
            // waited for the lock; if so the loop is about to dispose the
            // pipe, so silently drop this telemetry.
            var current = Volatile.Read(ref _activePipe);
            if (current is null || !current.IsConnected) return;
            await Framing.WriteFrameAsync(current, bytes, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* concurrent teardown won the race */ }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[helper-outbound] send {type} failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
#endif
