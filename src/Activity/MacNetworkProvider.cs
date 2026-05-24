using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sockets;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS network monitor using nettop. Only samples when subscribers exist.
/// </summary>
public sealed class MacNetworkProvider : BackgroundService, INetworkProvider
{
    private volatile IReadOnlyList<NetworkProcessInfo> _snapshot = Array.Empty<NetworkProcessInfo>();
    private int _intervalMs = 2000;
    private readonly MultiplexHub _hub;

    public MacNetworkProvider(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<NetworkProcessInfo> GetSnapshot() => _snapshot;
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
                    Sample();
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
        var output = ShellOut("/usr/bin/nettop", "-P", "-L", "1", "-J", "bytes_in,bytes_out", "-x");
        if (string.IsNullOrEmpty(output))
        {
            _snapshot = Array.Empty<NetworkProcessInfo>();
            return;
        }

        var merged = new Dictionary<string, (long bytesIn, long bytesOut)>();

        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 3)
                continue;

            var rawName = parts[0].Trim();
            var dotIdx = rawName.LastIndexOf('.');
            var name = dotIdx > 0 && int.TryParse(rawName.AsSpan(dotIdx + 1), out _)
                ? rawName.Substring(0, dotIdx)
                : rawName;

            if (string.IsNullOrEmpty(name))
                continue;

            if (!long.TryParse(parts[1].Trim(), out var bytesIn))
                continue;

            if (!long.TryParse(parts[2].Trim(), out var bytesOut))
                continue;

            if (merged.TryGetValue(name, out var prev))
                merged[name] = (prev.bytesIn + bytesIn, prev.bytesOut + bytesOut);
            else
                merged[name] = (bytesIn, bytesOut);
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

    private static string ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
                return "";

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try
                { proc.Kill(); }
                catch { }
            }
            return output;
        }
        catch
        {
            return "";
        }
    }
}
