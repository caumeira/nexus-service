using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Plugins.Transport;

/// <summary>
/// Unix-domain-socket listener for the plugin broker (mac / Linux). Binds the
/// socket inside a host-owned <c>0700</c> directory so only the running user can
/// reach it, and stamps every accepted connection with the kernel-attested peer
/// pid+uid (no plugin-supplied identity). The Windows side uses a named pipe;
/// both sit behind <see cref="IDuplexTransport"/>.
/// </summary>
public sealed partial class UnixSocketListener : IDuplexListener
{
    private readonly Socket _listener;

    public string Endpoint { get; }

    public UnixSocketListener(string socketPath)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("UnixSocketListener is mac/Linux only; use a named pipe on Windows.");

        Endpoint = socketPath;

        // Host-owned 0700 dir: the socket's reachability is gated by the
        // directory, which only the running user may traverse.
        var dir = Path.GetDirectoryName(socketPath)
            ?? throw new ArgumentException("socket path must include a directory", nameof(socketPath));
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // A stale socket file from a previous run blocks Bind; remove it.
        if (File.Exists(socketPath)) File.Delete(socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(16);
    }

    public async Task<IDuplexTransport> AcceptAsync(CancellationToken ct)
    {
        var sock = await _listener.AcceptAsync(ct).ConfigureAwait(false);
        try
        {
            var peer = ReadPeerCredentials(sock.Handle.ToInt32());
            return new UnixSocketTransport(sock, peer);
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        try { if (File.Exists(Endpoint)) File.Delete(Endpoint); } catch { /* best effort */ }
    }

    // ── kernel peer-credential read (getsockopt) ──

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(int sockfd, int level, int optname, [Out] byte[] optval, ref uint optlen);

    // Linux: getsockopt(SOL_SOCKET, SO_PEERCRED) -> struct ucred { pid, uid, gid }.
    private const int SOL_SOCKET = 1;
    private const int SO_PEERCRED = 17;
    // macOS: getsockopt(SOL_LOCAL, LOCAL_PEERCRED) -> xucred (uid at offset 4);
    //        getsockopt(SOL_LOCAL, LOCAL_PEERPID)  -> pid_t.
    private const int SOL_LOCAL = 0;
    private const int LOCAL_PEERCRED = 0x001;
    private const int LOCAL_PEERPID = 0x002;

    internal static PeerCredentials ReadPeerCredentials(int fd)
    {
        if (OperatingSystem.IsLinux())
        {
            var buf = new byte[12]; // pid_t + uid_t + gid_t, each 4 bytes LE
            uint len = (uint)buf.Length;
            if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, buf, ref len) != 0)
                throw new IOException($"SO_PEERCRED failed (errno {Marshal.GetLastPInvokeError()})");
            return new PeerCredentials(
                BinaryPrimitives.ReadInt32LittleEndian(buf),
                BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4)));
        }

        if (OperatingSystem.IsMacOS())
        {
            // xucred is < 128 bytes; uid is the second u_int (offset 4).
            var cred = new byte[128];
            uint clen = (uint)cred.Length;
            if (getsockopt(fd, SOL_LOCAL, LOCAL_PEERCRED, cred, ref clen) != 0)
                throw new IOException($"LOCAL_PEERCRED failed (errno {Marshal.GetLastPInvokeError()})");
            var uid = BinaryPrimitives.ReadInt32LittleEndian(cred.AsSpan(4));

            var pidBuf = new byte[4];
            uint plen = (uint)pidBuf.Length;
            if (getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, pidBuf, ref plen) != 0)
                throw new IOException($"LOCAL_PEERPID failed (errno {Marshal.GetLastPInvokeError()})");
            return new PeerCredentials(BinaryPrimitives.ReadInt32LittleEndian(pidBuf), uid);
        }

        throw new PlatformNotSupportedException("peer credentials require a Unix kernel");
    }
}

/// <summary>One accepted Unix-socket connection. Owns the socket; the framing
/// rides on <see cref="Stream"/>.</summary>
public sealed class UnixSocketTransport : IDuplexTransport
{
    public Stream Stream { get; }
    public PeerCredentials Peer { get; }

    internal UnixSocketTransport(Socket socket, PeerCredentials peer)
    {
        Peer = peer;
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    public void Dispose() => Stream.Dispose();
}
