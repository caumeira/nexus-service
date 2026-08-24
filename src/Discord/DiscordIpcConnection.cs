using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace Nexus.Service.Discord;

/// <summary>
/// One connection to the local Discord desktop client's RPC endpoint, carrying
/// Rich Presence only. Discord binds the first free endpoint of ten, so a
/// client probes 0..9 rather than assuming 0. Windows exposes them as named
/// pipes, every other platform as unix sockets under the user's runtime or
/// temp dir. The wire format is a 4-byte little-endian opcode, a 4-byte
/// little-endian payload length, then UTF-8 JSON.
/// </summary>
internal sealed class DiscordIpcConnection : IDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private const int EndpointProbeCount = 10;

    /// <summary>A real RPC frame is a few hundred bytes; this only bounds a corrupt length prefix.</summary>
    private const int MaxFrameBytes = 64 * 1024;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _readLoopCts = new();

    private volatile bool _closed;

    private DiscordIpcConnection(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>True once the far end hung up, the read loop faulted, or Dispose ran.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Probes every endpoint and returns the first one that completes the
    /// handshake. Null means Discord is not running (or is not listening yet),
    /// which is the ordinary case and not an error.
    /// </summary>
    /// <exception cref="DiscordIpcRejectedException">
    /// Discord answered but refused the handshake, which in practice means the
    /// client id is not a real application. Retrying cannot fix that.
    /// </exception>
    /// <param name="log">
    /// Receives one line per skipped endpoint. Without it an access-denied pipe
    /// (the Windows service is LocalSystem; Discord's pipe belongs to the
    /// logged-in user) is indistinguishable from "Discord is not running".
    /// </param>
    public static async Task<DiscordIpcConnection?> ConnectAsync(
        string clientId,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        for (var index = 0; index < EndpointProbeCount; index++)
        {
            var stream = await TryOpenAsync(index, cancellationToken, log).ConfigureAwait(false);
            if (stream is null)
            {
                continue;
            }

            try
            {
                return await AttachAsync(stream, clientId, cancellationToken).ConfigureAwait(false);
            }
            catch (DiscordIpcRejectedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A half-open or stale endpoint: keep probing the rest. macOS
                // in particular leaves the socket file behind after Discord
                // quits, so a connect that goes nowhere is expected.
                log?.Invoke($"endpoint {index} handshake failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// Handshakes over an already-open stream and starts the read loop. Split
    /// out from <see cref="ConnectAsync"/> so the wire behaviour can be tested
    /// against a stand-in endpoint without a running Discord.
    /// </summary>
    internal static async Task<DiscordIpcConnection> AttachAsync(Stream stream, string clientId, CancellationToken cancellationToken)
    {
        var connection = new DiscordIpcConnection(stream);
        try
        {
            await connection.HandshakeAsync(clientId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        connection.StartReadLoop();
        return connection;
    }

    /// <summary>
    /// Publishes an activity, or clears the current one when
    /// <paramref name="writeActivity"/> is null. Discord rate-limits this at
    /// roughly five calls per twenty seconds, so callers must not send on a
    /// timer - only on an actual change.
    /// </summary>
    public async Task SetActivityAsync(Action<Utf8JsonWriter>? writeActivity, CancellationToken cancellationToken)
    {
        var payload = BuildSetActivityPayload(writeActivity, Environment.ProcessId, Guid.NewGuid().ToString());
        await WriteFrameAsync(OpFrame, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a SET_ACTIVITY command payload. A null
    /// <paramref name="writeActivity"/> emits the explicit null activity that
    /// clears the current status.
    /// </summary>
    internal static byte[] BuildSetActivityPayload(Action<Utf8JsonWriter>? writeActivity, int processId, string nonce)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("cmd", "SET_ACTIVITY");
            writer.WriteString("nonce", nonce);
            writer.WriteStartObject("args");
            // Discord keys presence by the owning process and drops it when
            // that pid exits, so this is required even though we never fork.
            writer.WriteNumber("pid", processId);
            if (writeActivity is null)
            {
                writer.WriteNull("activity");
            }
            else
            {
                writer.WriteStartObject("activity");
                writeActivity(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Wraps a payload in the 4-byte opcode + 4-byte length header.</summary>
    internal static byte[] EncodeFrame(int opcode, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    private async Task HandshakeAsync(string clientId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("client_id", clientId);
            writer.WriteEndObject();
        }

        await WriteFrameAsync(OpHandshake, stream.ToArray(), timeout.Token).ConfigureAwait(false);

        // READY is the first frame on a good handshake. A CLOSE instead means
        // Discord parsed the frame and rejected its contents.
        while (true)
        {
            var frame = await ReadFrameAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("Discord closed the RPC connection during the handshake");

            if (frame.Opcode == OpClose)
            {
                throw new DiscordIpcRejectedException(DescribeClose(frame.Payload));
            }

            if (frame.Opcode != OpFrame)
            {
                continue;
            }

            using var document = JsonDocument.Parse(frame.Payload);
            if (document.RootElement.TryGetProperty("evt", out var evt)
                && evt.ValueKind == JsonValueKind.String
                && evt.GetString() == "READY")
            {
                return;
            }
        }
    }

    private void StartReadLoop()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_readLoopCts.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(_readLoopCts.Token).ConfigureAwait(false);
                    if (frame is null || frame.Value.Opcode == OpClose)
                    {
                        break;
                    }

                    if (frame.Value.Opcode == OpPing)
                    {
                        await WriteFrameAsync(OpPong, frame.Value.Payload, _readLoopCts.Token).ConfigureAwait(false);
                    }

                    // Command replies carry nothing the presence path needs;
                    // draining them is what keeps the pipe from backing up and
                    // is how a Discord quit is noticed while we sit idle.
                }
            }
            catch
            {
                // Any fault here is a dead connection, which _closed conveys.
            }
            finally
            {
                _closed = true;
            }
        }, CancellationToken.None);
    }

    private async Task WriteFrameAsync(int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        var frame = EncodeFrame(opcode, payload);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _closed = true;
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<DiscordFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Discord sent an out-of-range RPC frame length ({length})");
        }

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DiscordFrame(opcode, payload);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static async Task<Stream?> TryOpenAsync(int index, CancellationToken cancellationToken, Action<string>? log)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // Zero would still block until the pipe exists; a short wait
                // keeps a missing Discord from stalling the connect loop.
                await pipe.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception ex)
            {
                // TimeoutException is the ordinary "Discord is not running";
                // UnauthorizedAccessException is the LocalSystem/DACL case.
                if (ex is not TimeoutException)
                {
                    log?.Invoke($"pipe {index}: {ex.GetType().Name}: {ex.Message}");
                }
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        foreach (var directory in UnixSocketDirectories())
        {
            var path = Path.Combine(directory, $"discord-ipc-{index}");
            if (!File.Exists(path))
            {
                continue;
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"socket {path}: {ex.GetType().Name}: {ex.Message}");
                socket.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Every directory Discord is known to place its sockets in, most likely
    /// first. The sandboxed subdirectories are where Flatpak and snap builds
    /// land on Linux; they cost one File.Exists each and are never present
    /// on macOS.
    /// </summary>
    internal static IEnumerable<string> UnixSocketDirectories()
    {
        var root = FirstNonEmptyEnvironment("XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP") ?? "/tmp";
        yield return root;
        yield return Path.Combine(root, "app", "com.discordapp.Discord");
        yield return Path.Combine(root, "app", "com.discordapp.DiscordCanary");
        yield return Path.Combine(root, "snap.discord");
    }

    private static string? FirstNonEmptyEnvironment(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.TrimEnd('/');
            }
        }
        return null;
    }

    private static string DescribeClose(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "Discord rejected the connection";
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic message.
        }
        return "Discord rejected the connection";
    }

    public void Dispose()
    {
        _closed = true;
        try
        {
            _readLoopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _stream.Dispose();
        _readLoopCts.Dispose();
        _writeGate.Dispose();
    }

    private readonly record struct DiscordFrame(int Opcode, byte[] Payload);
}

/// <summary>
/// Discord answered the handshake and refused it. Distinct from a transport
/// fault because retrying an unknown client id never succeeds.
/// </summary>
internal sealed class DiscordIpcRejectedException : Exception
{
    public DiscordIpcRejectedException(string message) : base(message)
    {
    }
}
