using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Plugins.Transport;
using Xunit;

namespace Nexus.Service.Tests.Plugins;

public class UnixSocketTransportTests
{
    [Fact]
    public async Task Accept_reports_kernel_attested_peer_pid_and_round_trips()
    {
        // Unix-only; the Windows broker side is a named pipe. The runtime guard
        // also tells the platform analyzer the unix-only calls below are unreachable
        // on Windows (CA1416).
        if (OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "nexus-uds-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(dir, "broker.sock");
        using var listener = new UnixSocketListener(path);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = listener.AcceptAsync(cts.Token);

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);

        using var server = await acceptTask;

        // The client is THIS process, so the kernel must attest our own pid - the
        // proof the getsockopt peer-cred read works and isn't peer-supplied.
        Assert.Equal(Environment.ProcessId, server.Peer.ProcessId);
        Assert.True(server.Peer.UserId >= 0);

        // Byte round-trip over the framing-bearing stream.
        var sent = new byte[] { 1, 2, 3, 4, 5 };
        await client.SendAsync(sent, SocketFlags.None, cts.Token);
        var recv = new byte[sent.Length];
        var n = await server.Stream.ReadAsync(recv, cts.Token);
        Assert.Equal(sent.Length, n);
        Assert.Equal(sent, recv);

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Listener_creates_a_0700_socket_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "nexus-uds-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(dir, "broker.sock");
        using (var _ = new UnixSocketListener(path))
        {
            // Only the running user may traverse into the socket's directory.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dir));
        }

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
