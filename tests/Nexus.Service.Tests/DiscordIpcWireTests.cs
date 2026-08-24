using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nexus.Service.Discord;

namespace Nexus.Service.Tests;

/// <summary>
/// Drives the RPC transport against a stand-in endpoint over a real unix
/// socket, so the handshake and framing are exercised end to end without a
/// running Discord. The Windows named-pipe branch of ConnectAsync is not
/// covered here; only the stream behaviour above it is.
/// </summary>
public class DiscordIpcWireTests
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    [Fact]
    public async Task AttachAsync_SendsTheHandshakeAndWaitsForReady()
    {
        using var endpoint = await FakeDiscord.StartAsync();

        var connectTask = DiscordIpcConnection.AttachAsync(endpoint.ClientStream, "12345", CancellationToken.None);

        var (opcode, payload) = await endpoint.ReadFrameAsync();
        Assert.Equal(OpHandshake, opcode);
        using (var document = JsonDocument.Parse(payload))
        {
            Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
            Assert.Equal("12345", document.RootElement.GetProperty("client_id").GetString());
        }

        await endpoint.WriteFrameAsync(OpFrame, """{"cmd":"DISPATCH","evt":"READY","data":{}}""");

        using var connection = await connectTask;
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task AttachAsync_ThrowsRejectedWhenDiscordClosesTheHandshake()
    {
        using var endpoint = await FakeDiscord.StartAsync();

        var connectTask = DiscordIpcConnection.AttachAsync(endpoint.ClientStream, "bogus", CancellationToken.None);

        await endpoint.ReadFrameAsync();
        await endpoint.WriteFrameAsync(OpClose, """{"code":4000,"message":"Invalid client ID"}""");

        var error = await Assert.ThrowsAsync<DiscordIpcRejectedException>(() => connectTask);
        Assert.Contains("Invalid client ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActivityAsync_PublishesTheActivityOverTheWire()
    {
        using var endpoint = await FakeDiscord.StartAsync();
        using var connection = await ReadyConnectionAsync(endpoint);

        await connection.SetActivityAsync(
            writer => DiscordRichPresence.WriteActivity(writer, "Watching temps", 99),
            CancellationToken.None);

        var (opcode, payload) = await endpoint.ReadFrameAsync();
        Assert.Equal(OpFrame, opcode);

        using var document = JsonDocument.Parse(payload);
        var activity = document.RootElement.GetProperty("args").GetProperty("activity");
        Assert.Equal("SET_ACTIVITY", document.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("Watching temps", activity.GetProperty("details").GetString());
        Assert.Equal(99, activity.GetProperty("timestamps").GetProperty("start").GetInt64());
    }

    [Fact]
    public async Task ReadLoop_AnswersAPingWithAPong()
    {
        using var endpoint = await FakeDiscord.StartAsync();
        using var connection = await ReadyConnectionAsync(endpoint);

        await endpoint.WriteFrameAsync(OpPing, """{"nonce":"abc"}""");

        var (opcode, payload) = await endpoint.ReadFrameAsync();
        Assert.Equal(OpPong, opcode);
        // Discord expects its own ping payload echoed back verbatim.
        Assert.Equal("""{"nonce":"abc"}""", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public async Task IsClosed_FlipsWhenDiscordHangsUp()
    {
        using var endpoint = await FakeDiscord.StartAsync();
        using var connection = await ReadyConnectionAsync(endpoint);
        Assert.False(connection.IsClosed);

        endpoint.HangUp();

        // The read loop notices the EOF; give it a bounded moment to run.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!connection.IsClosed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(connection.IsClosed);
    }

    private static async Task<DiscordIpcConnection> ReadyConnectionAsync(FakeDiscord endpoint)
    {
        var connectTask = DiscordIpcConnection.AttachAsync(endpoint.ClientStream, "12345", CancellationToken.None);
        await endpoint.ReadFrameAsync();
        await endpoint.WriteFrameAsync(OpFrame, """{"cmd":"DISPATCH","evt":"READY","data":{}}""");
        return await connectTask;
    }

    /// <summary>A connected unix socket pair standing in for the Discord client.</summary>
    private sealed class FakeDiscord : IDisposable
    {
        private readonly string _path;
        private readonly Socket _server;
        private readonly Socket _client;

        private FakeDiscord(string path, Socket server, Socket client, NetworkStream clientStream)
        {
            _path = path;
            _server = server;
            _client = client;
            ClientStream = clientStream;
        }

        /// <summary>The stream the production code talks over.</summary>
        public NetworkStream ClientStream { get; }

        public static async Task<FakeDiscord> StartAsync()
        {
            // Unix socket paths are length-capped, so this stays short rather
            // than nesting under the test's working directory.
            var path = Path.Combine(Path.GetTempPath(), $"nx-{Guid.NewGuid().ToString("N")[..8]}.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);

            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connect = client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            var server = await listener.AcceptAsync();
            await connect;

            return new FakeDiscord(path, server, client, new NetworkStream(client, ownsSocket: true));
        }

        public async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
        {
            var header = await ReadExactAsync(8);
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var payload = length > 0 ? await ReadExactAsync(length) : [];
            return (opcode, payload);
        }

        public async Task WriteFrameAsync(int opcode, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
            payload.CopyTo(frame.AsSpan(8));
            await _server.SendAsync(frame);
        }

        public void HangUp() => _server.Shutdown(SocketShutdown.Both);

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await _server.ReceiveAsync(buffer.AsMemory(offset));
                if (read == 0)
                {
                    throw new IOException("the stand-in endpoint hung up early");
                }
                offset += read;
            }
            return buffer;
        }

        public void Dispose()
        {
            ClientStream.Dispose();
            _client.Dispose();
            _server.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}
