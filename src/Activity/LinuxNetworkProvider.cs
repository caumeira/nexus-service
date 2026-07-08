using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux network/IO monitor. The kernel has no per-process network byte counter
/// without eBPF/cgroups, so the byte totals are approximated from rchar/wchar in
/// <c>/proc/[pid]/io</c> - but only for processes that actually own a TCP socket
/// to a non-loopback peer (resolved via <c>/proc/net/tcp{,6}</c> → socket inode →
/// <c>/proc/[pid]/fd</c>). That keeps disk-bound processes, pure loopback
/// chatter, and our own PID off the list, mirroring the Windows provider's
/// "active non-loopback TCP connection" gate (<c>TcpPeerFilter</c>).
///
/// Pure file reads - no subprocesses. Only samples when the "network" or
/// "monitoring" WS topics have subscribers.
/// </summary>
public sealed class LinuxNetworkProvider : BackgroundService, INetworkProvider
{
    private static readonly int OwnPid = Environment.ProcessId;

    private volatile IReadOnlyList<NetworkProcessInfo> _snapshot = Array.Empty<NetworkProcessInfo>();
    private int _intervalMs = 2000;
    private readonly MultiplexHub _hub;

    public LinuxNetworkProvider(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<NetworkProcessInfo> GetSnapshot()
    {
        // REST clients (/api/network/top) don't subscribe to WS topics, so the
        // background sampler never runs for them. Populate on-demand if empty.
        if (_snapshot.Count == 0)
        {
            try
            { Sample(); }
            catch { }
        }
        return _snapshot;
    }
    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    private bool HasSubscribers =>
        _hub.TopicHasSubscribers("network") || _hub.TopicHasSubscribers("monitoring");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasSubscribers)
                {
                    Sample();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[network] sample failed: {ex.Message}");
            }

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Sample()
    {
        // Only attribute processes that actually own a non-loopback network
        // socket; /proc/[pid]/io counts ALL syscall I/O (disk + pipe + socket),
        // so without this gate the list is dominated by compilers, browsers
        // writing cache, and our own SQLite - not network talkers.
        var active = ActiveNetworkPids();
        active.Remove(OwnPid);
        if (active.Count == 0)
        {
            _snapshot = Array.Empty<NetworkProcessInfo>();
            return;
        }

        var merged = new Dictionary<string, (long bytesIn, long bytesOut)>(active.Count);
        foreach (var dir in EnumeratePidDirs())
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || !active.Contains(pid))
            {
                continue;
            }

            var name = ReadComm(dir);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var (rchar, wchar) = ReadIoCounters(dir);
            if (rchar == 0 && wchar == 0)
            {
                continue;
            }

            if (merged.TryGetValue(name, out var prev))
            {
                merged[name] = (prev.bytesIn + rchar, prev.bytesOut + wchar);
            }
            else
            {
                merged[name] = (rchar, wchar);
            }
        }

        _snapshot = merged
            .Where(kv => kv.Value.bytesIn + kv.Value.bytesOut > 0)
            .OrderByDescending(kv => kv.Value.bytesIn + kv.Value.bytesOut)
            .Select(kv => new NetworkProcessInfo
            {
                Name = kv.Key,
                BytesIn = kv.Value.bytesIn,
                BytesOut = kv.Value.bytesOut,
            })
            .ToList();
    }

    /// <summary>
    /// PIDs that own at least one TCP socket connected to a non-loopback peer (a
    /// real network conversation): /proc/net/tcp{,6} active rows → socket inode →
    /// /proc/[pid]/fd reverse lookup. Listening-only and loopback-only sockets
    /// are excluded.
    /// </summary>
    private static HashSet<int> ActiveNetworkPids()
    {
        var inodes = new HashSet<long>();
        ReadActiveInodes("/proc/net/tcp", inodes);
        ReadActiveInodes("/proc/net/tcp6", inodes);
        var pids = new HashSet<int>();
        if (inodes.Count == 0)
        {
            return pids;
        }

        foreach (var dir in EnumeratePidDirs())
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                {
                    var target = ReadLink(fd);
                    // "socket:[12345]"
                    if (target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
                    {
                        var inner = target.AsSpan(8, target.Length - 9);
                        if (long.TryParse(inner, out var ino) && inodes.Contains(ino))
                        {
                            pids.Add(pid);
                            break;
                        }
                    }
                }
            }
            catch { /* pid vanished or fd dir unreadable - skip */ }
        }
        return pids;
    }

    private static void ReadActiveInodes(string procNetPath, HashSet<long> inodes)
    {
        string[] lines;
        try { lines = File.ReadAllLines(procNetPath); }
        catch { return; }
        // Columns: sl local_address rem_address st ... uid timeout inode ...
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10)
            {
                continue;
            }
            if (f[3] == "0A") // TCP LISTEN - not an active conversation
            {
                continue;
            }
            if (!IsRemoteRoutable(f[2]))
            {
                continue;
            }
            if (long.TryParse(f[9], out var inode) && inode != 0)
            {
                inodes.Add(inode);
            }
        }
    }

    /// <summary>
    /// True if a /proc/net rem_address hex ("0100007F:0050" v4, 32-hex v6) is a
    /// real peer - non-zero and not loopback (127.0.0.0/8 or ::1). IPv4 is
    /// little-endian, so 127.x shows as a trailing "7F".
    /// </summary>
    internal static bool IsRemoteRoutable(string remHex)
    {
        var colon = remHex.IndexOf(':');
        var addr = colon >= 0 ? remHex[..colon] : remHex;
        if (addr.Length == 8) // IPv4
        {
            if (addr == "00000000") return false;                            // 0.0.0.0 (unconnected)
            return !addr.EndsWith("7F", StringComparison.OrdinalIgnoreCase); // 127.x.x.x loopback
        }
        if (addr.Length == 32) // IPv6
        {
            if (addr.TrimStart('0').Length == 0) return false;               // ::
            return addr != "00000000000000000000000001000000";              // ::1 loopback
        }
        return false;
    }

    private static string ReadLink(string path)
    {
        try { return new FileInfo(path).LinkTarget ?? ""; }
        catch { return ""; }
    }

    private static IEnumerable<string> EnumeratePidDirs()
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch
        {
            yield break;
        }
        foreach (var d in entries)
        {
            var basename = Path.GetFileName(d);
            if (basename.Length == 0 || !char.IsDigit(basename[0]))
            {
                continue;
            }
            yield return d;
        }
    }

    private static string ReadComm(string pidDir)
    {
        try
        {
            var path = Path.Combine(pidDir, "comm");
            if (!File.Exists(path))
            {
                return "";
            }
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static (long rchar, long wchar) ReadIoCounters(string pidDir)
    {
        try
        {
            var path = Path.Combine(pidDir, "io");
            if (!File.Exists(path))
            {
                return (0, 0);
            }
            long rchar = 0;
            long wchar = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("rchar:", StringComparison.Ordinal))
                {
                    long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out rchar);
                }
                else if (line.StartsWith("wchar:", StringComparison.Ordinal))
                {
                    long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out wchar);
                    break; // rchar comes before wchar; safe to bail after wchar.
                }
            }
            return (rchar, wchar);
        }
        catch
        {
            return (0, 0);
        }
    }
}
