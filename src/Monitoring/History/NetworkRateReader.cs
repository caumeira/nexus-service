using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace Nexus.Service.Monitoring.History;

/// <summary>One byte/s rate reading; both null when no prior reading exists
/// yet to diff against, or the diff was rejected (see ComputeRate).</summary>
public readonly record struct NetworkRate(double? InBytesPerSec, double? OutBytesPerSec);

/// <summary>
/// System-wide IPv4 byte-rate gauge sampled by MetricsSampler each tick.
/// Sums cumulative BytesReceived/BytesSent across every "Up", non-loopback,
/// non-tunnel NIC and diffs against the previous read using
/// Environment.TickCount64 (monotonic, immune to wall-clock adjustments).
/// Independent of the gated per-process INetworkProvider (WindowsNetworkProvider
/// et al.), which churns process handles and TCP table reads on-demand - this
/// reader only touches NetworkInterface counters, a different I/O class.
/// </summary>
public sealed class NetworkRateReader
{
    // A reading gap wider than this (missed tick, system suspend) means the
    // delta no longer reflects a 1-second rate, so the read is discarded
    // instead of reported as a rate spike/trough.
    private const long MaxElapsedMs = 5000;

    private long _lastTicks = -1;
    private long _lastBytesIn;
    private long _lastBytesOut;

    public NetworkRate Read()
    {
        var (bytesIn, bytesOut) = ReadCumulativeCounters();
        var nowTicks = System.Environment.TickCount64;
        var rate = ComputeRate(_lastTicks, _lastBytesIn, _lastBytesOut, nowTicks, bytesIn, bytesOut);
        _lastTicks = nowTicks;
        _lastBytesIn = bytesIn;
        _lastBytesOut = bytesOut;
        return rate;
    }

    /// <summary>Pure delta math: null on the first read (lastTicks &lt; 0),
    /// a non-positive or &gt;5s elapsed gap, or a negative byte delta (NIC
    /// counter reset, e.g. adapter re-enumerated).</summary>
    internal static NetworkRate ComputeRate(
        long lastTicks, long lastBytesIn, long lastBytesOut,
        long nowTicks, long nowBytesIn, long nowBytesOut)
    {
        if (lastTicks < 0)
        {
            return new NetworkRate(null, null);
        }

        var elapsedMs = nowTicks - lastTicks;
        if (elapsedMs <= 0 || elapsedMs > MaxElapsedMs)
        {
            return new NetworkRate(null, null);
        }

        var deltaIn = nowBytesIn - lastBytesIn;
        var deltaOut = nowBytesOut - lastBytesOut;
        if (deltaIn < 0 || deltaOut < 0)
        {
            return new NetworkRate(null, null);
        }

        var seconds = elapsedMs / 1000.0;
        return new NetworkRate(deltaIn / seconds, deltaOut / seconds);
    }

    private static (long BytesIn, long BytesOut) ReadCumulativeCounters()
    {
        long bytesIn = 0;
        long bytesOut = 0;
        IReadOnlyList<NetworkInterface> nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return (0, 0);
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            try
            {
                var stats = nic.GetIPv4Statistics();
                bytesIn += stats.BytesReceived;
                bytesOut += stats.BytesSent;
            }
            catch
            {
                // NIC vanished mid-enumeration (USB adapter unplugged) - skip it.
            }
        }
        return (bytesIn, bytesOut);
    }
}
