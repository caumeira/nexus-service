#if LINUX
using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Sums cumulative disk bytes read/written across physical block devices from
/// /proc/diskstats. Whole disks only: a device counts when /sys/block lists
/// it (partitions are absent there) and its name is not a virtual class
/// (loop/ram/zram/dm-/md/sr/fd), so partition and RAID/mapper traffic is not
/// double-counted. DiskRateReader diffs consecutive reads into a byte rate.
/// </summary>
internal static class LinuxDiskStats
{
    private const string DiskstatsPath = "/proc/diskstats";
    private const string SysBlockPath = "/sys/block";

    // /proc/diskstats sector fields are fixed 512-byte units regardless of
    // the device's logical sector size (kernel ABI).
    private const long SectorBytes = 512;

    private static readonly string[] VirtualPrefixes = ["loop", "ram", "zram", "dm-", "md", "sr", "fd"];

    public static bool TryReadCumulativeBytes(out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        try
        {
            return TryParse(File.ReadAllText(DiskstatsPath), ListPhysicalDisks(), out bytesRead, out bytesWritten);
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> ListPhysicalDisks()
    {
        var disks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(SysBlockPath))
        {
            var name = Path.GetFileName(dir);
            if (!IsVirtual(name))
            {
                disks.Add(name);
            }
        }
        return disks;
    }

    private static bool IsVirtual(string name)
    {
        foreach (var prefix in VirtualPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>False when no listed disk produced a parsable line; a disk
    /// that detaches between reads shrinks the sums, which the caller's
    /// negative-delta guard discards.</summary>
    internal static bool TryParse(string diskstats, IReadOnlySet<string> disks, out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        var anyRead = false;

        foreach (var line in diskstats.Split('\n'))
        {
            // Fields: major minor name reads-completed reads-merged
            // sectors-read ms-reading writes-completed writes-merged
            // sectors-written ... (Documentation/admin-guide/iostats.rst).
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10 || !disks.Contains(parts[2]))
            {
                continue;
            }

            if (!long.TryParse(parts[5], out var sectorsRead) ||
                !long.TryParse(parts[9], out var sectorsWritten))
            {
                continue;
            }

            bytesRead += sectorsRead * SectorBytes;
            bytesWritten += sectorsWritten * SectorBytes;
            anyRead = true;
        }

        return anyRead;
    }
}
#endif
