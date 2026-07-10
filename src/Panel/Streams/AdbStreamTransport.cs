using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// D213 transport: prepares the on-device player blob and fifo over short-lived
/// adb.exe subprocesses, then opens a persistent raw adb-server-protocol socket
/// that holds <c>cat &gt; fifo</c> open on the device for the life of the stream.
///
/// The device's adbd has no <c>exec:</c> service and its shell pty mangles binary,
/// so the byte pipe cannot go through adb.exe's own stdin - it must speak the adb
/// server protocol directly on 127.0.0.1:5037 (see <see cref="AdbServerProtocol"/>),
/// selecting the device with <c>host:transport:&lt;serial&gt;</c> so a Q-series
/// panel sharing the same adb server is never targeted by mistake.
/// </summary>
public sealed class AdbStreamTransport : IStreamedPanelTransport
{
    // Extension picks the on-device parser; must stay ".264".
    private const string FifoPath = "/tmp/v.264";
    private const int AdbServerPort = 5037;
    private const string Q60BlobKind = "d213-q60";
    private const int BlobPushAttempts = 3;

    private readonly StreamedPanelDeviceInfo _info;
    private readonly string? _adbPathOverride;

    private string? _adbPath;
    private string? _remotePlayerPath;
    private Socket? _streamSocket;
    private Process? _playerProcess;

    public bool IsOpen { get; private set; }

    public string Serial => _info.Serial;

    public AdbStreamTransport(StreamedPanelDeviceInfo info, string? adbPathOverride = null)
    {
        _info = info;
        _adbPathOverride = adbPathOverride;
    }

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        _adbPath = _adbPathOverride ?? AdbLocator.ResolveAdbPath()
            ?? throw new InvalidOperationException(
                "adb not found (no bundled copy, PATH entry, or Android SDK); cannot open D213 transport");

        var (localBlobPath, remoteBlobPath) = ResolveBlob(_info.Profile.Kind);
        if (!File.Exists(localBlobPath))
        {
            ServiceLog.Warn($"[d213] {Serial}: player blob missing at {localBlobPath} (dev build without publish?)");
            throw new InvalidOperationException($"D213 player blob missing at {localBlobPath}");
        }

        EnsureBlobOnDevice(localBlobPath, remoteBlobPath);
        // adb push never sets the execute bit; without this the player exec
        // fails "Permission denied" and the fifo has no reader.
        RunAdbShell($"chmod 755 {remoteBlobPath}");
        _remotePlayerPath = remoteBlobPath;

        KillStalePlayers();
        BlankFramebuffer();
        EnsureFifo();

        _streamSocket = OpenRawStreamSocket();
        IsOpen = true;
        ServiceLog.Info($"[d213] {Serial}: transport open (fifo {FifoPath})");
    }

    public void StartPlayer()
    {
        if (!IsOpen || _adbPath is null || _remotePlayerPath is null)
        {
            throw new InvalidOperationException("D213 transport must be open before starting the player");
        }

        var psi = new ProcessStartInfo(_adbPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_adbPath) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(Serial);
        psi.ArgumentList.Add("shell");
        psi.ArgumentList.Add($"{_remotePlayerPath} -i {FifoPath} 2>&1");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("adb shell process for the D213 player failed to start");
        _playerProcess = process;

        // mpp_video_test is silent and has no watchdog; its stdout/stderr must
        // still be drained so the on-device shell never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        // The adb shell spawn cannot report an on-device exec failure (exit
        // text is drained, not parsed), and a dead player means the fifo has
        // no reader and every write stalls. pidof is the only observable
        // player-alive signal over adb; poll it briefly.
        var playerName = _remotePlayerPath[(_remotePlayerPath.LastIndexOf('/') + 1)..];
        var alive = false;
        for (var attempt = 0; attempt < 5 && !alive; attempt++)
        {
            System.Threading.Thread.Sleep(200);
            var (ok, stdout, _) = RunAdbShell($"pidof {playerName}");
            alive = ok && stdout.Trim().Length > 0;
        }
        if (!alive)
        {
            try { process.Kill(true); } catch { }
            _playerProcess = null;
            throw new InvalidOperationException($"D213 player {playerName} did not start (no pid on device)");
        }

        ServiceLog.Info($"[d213] {Serial}: player started ({_remotePlayerPath})");
    }

    public void Write(ReadOnlySpan<byte> annexBAccessUnit)
    {
        var socket = _streamSocket ?? throw new InvalidOperationException("D213 transport not open");
        var offset = 0;
        while (offset < annexBAccessUnit.Length)
        {
            offset += socket.Send(annexBAccessUnit[offset..]);
        }
    }

    public void Dispose()
    {
        IsOpen = false;

        try { _streamSocket?.Close(); } catch { }
        _streamSocket = null;

        try { _playerProcess?.Kill(true); } catch { }
        try { _playerProcess?.Dispose(); } catch { }
        _playerProcess = null;

        KillRemotePlayerBestEffort();
    }

    private static (string LocalPath, string RemotePath) ResolveBlob(string kind)
    {
        if (kind == Q60BlobKind)
        {
            return (Path.Combine(AppContext.BaseDirectory, "tools", "d213", "mpp_q60.bin"), "/tmp/mpp_q60");
        }
        return (Path.Combine(AppContext.BaseDirectory, "tools", "d213", "mpp_video_fs.bin"), "/tmp/mpp_fs");
    }

    /// <summary>
    /// Pushes over adb can silently corrupt on this board's marginal USB link
    /// with a success exit, so every push is verified against the device's own
    /// md5sum rather than trusted from adb's exit code.
    /// </summary>
    private void EnsureBlobOnDevice(string localPath, string remotePath)
    {
        var localMd5 = ComputeLocalMd5(localPath);
        for (var attempt = 1; attempt <= BlobPushAttempts; attempt++)
        {
            if (MatchesRemoteMd5(remotePath, localMd5))
            {
                return;
            }
            PushBlob(localPath, remotePath);
            if (MatchesRemoteMd5(remotePath, localMd5))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"D213 player blob failed md5 verification after {BlobPushAttempts} push attempts ({remotePath})");
    }

    private bool MatchesRemoteMd5(string remotePath, string localMd5)
    {
        var remoteMd5 = TryReadRemoteMd5(remotePath);
        return remoteMd5 is not null && string.Equals(remoteMd5, localMd5, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeLocalMd5(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = MD5.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private string? TryReadRemoteMd5(string remotePath)
    {
        var (ok, stdout, _) = RunAdbShell($"md5sum {remotePath} 2>/dev/null");
        if (!ok)
        {
            return null;
        }
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        var spaceIdx = trimmed.IndexOf(' ');
        var hex = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        return hex.Length == 32 ? hex : null;
    }

    private void PushBlob(string localPath, string remotePath)
    {
        var psi = new ProcessStartInfo(_adbPath!)
        {
            WorkingDirectory = Path.GetDirectoryName(_adbPath!) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(Serial);
        psi.ArgumentList.Add("push");
        psi.ArgumentList.Add(localPath);
        psi.ArgumentList.Add(remotePath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return;
        }
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(true); } catch { }
        }
    }

    private void KillStalePlayers()
    {
        // test_lvgl is the stock boot demo; it repaints the fb continuously,
        // so leaving it alive defeats the blank below and its animation shows
        // in the bars around any non-fullscreen video window.
        RunAdbShell("killall player_demo mpp_video_test mpp_fs mpp_q60 cat httpd test_lvgl 2>/dev/null; true");
    }

    // Video renders on the DE's own layer; the fb below still shows the dead
    // boot UI around a non-fullscreen video window (q60 profile side bars).
    // Blank it black; the pan write matters because AIC fbdev repaints only
    // on the pan ioctl. Best-effort.
    private void BlankFramebuffer()
    {
        RunAdbShell("dd if=/dev/zero of=/dev/fb0 2>/dev/null; echo 0,0 > /sys/class/graphics/fb0/pan; true");
    }

    private void EnsureFifo()
    {
        var (ok, _, stderr) = RunAdbShell($"test -p {FifoPath} || busybox mkfifo {FifoPath}");
        if (!ok)
        {
            throw new InvalidOperationException($"D213 fifo ensure failed: {stderr}");
        }
    }

    private void KillRemotePlayerBestEffort()
    {
        if (_adbPath is null)
        {
            return;
        }
        try
        {
            RunAdbShell("killall mpp_fs mpp_q60 cat 2>/dev/null; true", timeoutMs: 5_000);
        }
        catch { }
    }

    private (bool Success, string Stdout, string Stderr) RunAdbShell(string shellCommand, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo(_adbPath!)
        {
            WorkingDirectory = Path.GetDirectoryName(_adbPath!) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(Serial);
        psi.ArgumentList.Add("shell");
        psi.ArgumentList.Add(shellCommand);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (false, string.Empty, "Process.Start returned null");
        }
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            return (false, string.Empty, $"timed out after {timeoutMs}ms");
        }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        return (process.ExitCode == 0, stdout, stderr);
    }

    private void EnsureAdbServerRunning()
    {
        var psi = new ProcessStartInfo(_adbPath!)
        {
            WorkingDirectory = Path.GetDirectoryName(_adbPath!) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("start-server");
        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);
        }
        catch { }
    }

    private Socket OpenRawStreamSocket()
    {
        EnsureAdbServerRunning();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, AdbServerPort);
            socket.SendTimeout = 2000;
            // Bounds the OKAY/READY handshake reads; an unbounded Receive here
            // hangs the coordinator tick thread for every device when the
            // device shell stalls mid-handshake.
            socket.ReceiveTimeout = 5000;

            SendServiceRequest(socket, $"host:transport:{Serial}");
            SendServiceRequest(socket, $"shell:stty raw -echo; echo READY; cat > {FifoPath}");

            WaitForReady(socket);
            return socket;
        }
        catch
        {
            socket.Close();
            throw;
        }
    }

    private static void SendServiceRequest(Socket socket, string request)
    {
        socket.Send(AdbServerProtocol.EncodeRequest(request));

        var buffer = new byte[512];
        var received = 0;
        while (true)
        {
            var read = socket.Receive(buffer.AsSpan(received));
            if (read <= 0)
            {
                throw new IOException($"adb server closed the connection responding to '{request}'");
            }
            received += read;
            if (AdbServerProtocol.TryParseStatus(buffer.AsSpan(0, received), out var status))
            {
                if (!status.Ok)
                {
                    throw new IOException($"adb service '{request}' failed: {status.FailMessage}");
                }
                return;
            }
            if (received == buffer.Length)
            {
                throw new IOException($"adb server status response exceeded buffer for '{request}'");
            }
        }
    }

    private static void WaitForReady(Socket socket)
    {
        var scanner = new ReadyScanner();
        var buffer = new byte[64];
        while (!scanner.Seen)
        {
            var read = socket.Receive(buffer);
            if (read <= 0)
            {
                throw new IOException("adb shell stream closed before READY");
            }
            scanner.Feed(buffer.AsSpan(0, read));
        }
    }
}
