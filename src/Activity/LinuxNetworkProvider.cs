using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Activity;
using Qos.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Qos.Service.Activity;

/// <summary>
/// Linux network/IO monitor. Mirrors the Windows semantic
/// (<c>GetProcessIoCounters</c>: total process I/O, not network-only) by reading
/// <c>/proc/[pid]/io</c> for every PID and mapping <c>rchar → BytesIn</c>,
/// <c>wchar → BytesOut</c>.
///
/// Pure file reads — no subprocesses. Only samples when the "network" or
/// "monitoring" WS topics have subscribers.
/// </summary>
public sealed class LinuxNetworkProvider : BackgroundService, INetworkProvider
{
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
        var merged = new Dictionary<string, (long bytesIn, long bytesOut)>(256);
        foreach (var dir in EnumeratePidDirs())
        {
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
