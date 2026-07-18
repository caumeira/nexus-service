using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IAppUsageHistoryStore, the storage-dominant per-app
/// tier: a global AppNameDictionary shared by every metric, plus independent
/// metric kinds (cpu, memory, gpu, vram, storage, net, storage-read,
/// storage-write, net-down, net-up) each stored as append-only per-UTC-day
/// segment files
/// under their own directory. storage/net and their read-write/down-up
/// splits carry no gpu dimension and share one wire format: raw bytes/sec
/// stored as an int64 (StorageRecordWidth), wider than the x10 fixed-point
/// int32 SimpleRecordWidth uses for cpu/mem, so a disk or network throughput
/// reading past roughly 2 GB/s stays representable instead of overflowing.
/// Retention drops whole day files once
/// every tick they hold is older than the prune cutoff; PruneFloorSec then
/// hides whatever remains of a day file straddling that cutoff at read
/// time, so a query never sees a pruned tick even though the day itself is
/// still on disk.
///
/// No day-seal sorted-block table: a day segment is a flat append log of
/// tick records (see WriteSimpleTick/WriteGpuTick/WriteVramTick/WriteStorageTick),
/// and every query does a linear scan over the days it touches, translating
/// each record's per-day local app id back to the global id via that day's
/// AppLocalIdMap. Query windows are bounded by MetricsHistory.RetentionDays
/// (7 days), and even a fully-saturated day segment is a few MB, so this
/// trades a small amount of read-side CPU for a format an order of
/// magnitude simpler than a sorted per-app block table - see
/// AppUsageStorageEstimateTests for the measured footprint.
///
/// AppUsageStore is the sole writer (Append), the same single-writer
/// assumption the rest of the binary store is built on; day segment reads
/// open a fresh FileStream/read the whole file per call rather than holding
/// long-lived handles, since Append/Query both run at most a few times a
/// minute, not per tick - simpler lifetime management than the per-entity
/// ring files GpuRingStore/FanRingStore/TempComponentRingStore keep open for
/// the store's whole lifetime, at a cost this call rate never notices.
///
/// Because there is no long-lived writer handle, a torn trailing tick record
/// from a crash mid-append is not caught at store-open time the way
/// AppNameDictionary/AppLocalIdMap/EntityRegistry catch theirs - instead
/// TruncateTornTail runs at the start of every Write*Day call, immediately
/// before it appends, so a day segment is always clean before new records
/// land after it.
/// </summary>
internal sealed class AppUsageStore : IDisposable
{
    private enum AppMetricKind
    {
        Cpu = 0, Mem = 1, Gpu = 2, Vram = 3, Storage = 4, Net = 5,
        StorageRead = 6, StorageWrite = 7, NetDown = 8, NetUp = 9,
    }

    private static readonly string[] KindDirNames =
        { "cpu", "mem", "gpu", "vram", "storage", "net", "storage-read", "storage-write", "net-down", "net-up" };
    private static readonly AppMetricKind[] AllKinds =
    {
        AppMetricKind.Cpu, AppMetricKind.Mem, AppMetricKind.Gpu, AppMetricKind.Vram, AppMetricKind.Storage, AppMetricKind.Net,
        AppMetricKind.StorageRead, AppMetricKind.StorageWrite, AppMetricKind.NetDown, AppMetricKind.NetUp,
    };

    private const int SecondsPerDay = 86_400;

    // Per-record byte width for each day-segment format, shared between each
    // kind's Write*Tick/Read*Day pair and TruncateTornTail below.
    private const int SimpleRecordWidth = 6;
    private const int GpuRecordWidth = 10;
    private const int VramRecordWidth = 8;
    // Storage/net and their read-write/down-up splits all carry raw
    // bytes/sec as an int64, rather than the x10 fixed-point percent
    // SimpleRecordWidth's int32 slot holds - disk/network throughput on a
    // fast NVMe drive or NIC exceeds int32 range. Shared by
    // WriteStorageDay/ReadStorageDay below, parameterized over which of the
    // six kinds is being written/read.
    private const int StorageRecordWidth = 10;

    private readonly record struct AppEntry(int GlobalId, int GpuIndex, double Value, double? VramMb);

    private readonly string _dir;
    private readonly Func<string, int?> _resolveGpuIndex;
    private readonly AppNameDictionary _names;
    private readonly string _pruneFloorPath;
    private long _pruneFloorSec;

    /// <param name="dir">Directory this store owns exclusively (matches
    /// GpuRingStore/TempComponentRingStore's own per-store directory
    /// convention).</param>
    /// <param name="resolveGpuIndex">Resolves a sanitized gpu id to the
    /// scalar side's GpuRingStore ring index, or null if the scalar store has
    /// never registered it: an app-usage gpu/vram sample for a gid with no
    /// scalar registration is dropped rather than minting an orphaned
    /// identity for it.</param>
    public AppUsageStore(string dir, Func<string, int?> resolveGpuIndex)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _resolveGpuIndex = resolveGpuIndex;
        _names = AppNameDictionary.Open(Path.Combine(dir, "apps.dict"));
        _pruneFloorPath = Path.Combine(dir, "prune.floor");
        _pruneFloorSec = LoadPruneFloor(_pruneFloorPath);

        foreach (var name in KindDirNames)
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }
    }

    /// <summary>Timestamps below this floor read as absent regardless of
    /// what a boundary day file still physically holds - the read-time half
    /// of retention, matching RingFile.PruneFloorSec's role for the
    /// fixed-slot tiers (see the class doc's "hardest tier" note on why whole
    /// day files, not individual rows, are what Append below actually
    /// deletes).</summary>
    public long PruneFloorSec
    {
        get => Volatile.Read(ref _pruneFloorSec);
        private set => Volatile.Write(ref _pruneFloorSec, value);
    }

    private static long FloorToDay(long tsSec) => tsSec / SecondsPerDay;

    private string KindDir(AppMetricKind kind) => Path.Combine(_dir, KindDirNames[(int)kind]);
    private string SegPath(AppMetricKind kind, long day) => Path.Combine(KindDir(kind), $"{day}.seg");
    private string IdsPath(AppMetricKind kind, long day) => Path.Combine(KindDir(kind), $"{day}.ids");

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        if (ticks.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        if (ticks.Count > 0)
        {
            var cpuByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, int ValueX10)> Apps)>>();
            var memByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, int ValueX10)> Apps)>>();
            var gpuByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, int GpuIndex, short LoadX10, int VramMbOrSentinel)> Apps)>>();
            var vramByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, int GpuIndex, int ValueMb)> Apps)>>();
            var storageByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();
            var netByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();
            var storageReadByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();
            var storageWriteByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();
            var netDownByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();
            var netUpByDay = new Dictionary<long, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)>>();

            // Name resolution happens here, in tick-array order (not sorted
            // by ts) - first-seen casing depends on iteration order, not
            // chronological order, when a batch itself carries out-of-order
            // ticks.
            foreach (var tick in ticks)
            {
                var day = FloorToDay(tick.TsSec);
                List<(int, int)>? cpuEntries = null;
                List<(int, int)>? memEntries = null;
                List<(int, int, short, int)>? gpuEntries = null;
                List<(int, int, int)>? vramEntries = null;
                List<(int, long)>? storageEntries = null;
                List<(int, long)>? netEntries = null;
                List<(int, long)>? storageReadEntries = null;
                List<(int, long)>? storageWriteEntries = null;
                List<(int, long)>? netDownEntries = null;
                List<(int, long)>? netUpEntries = null;

                foreach (var metric in tick.Metrics)
                {
                    if (metric.Metric == "cpu")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        cpuEntries = new List<(int, int)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            cpuEntries.Add((_names.RegisterOrGet(a.Name), ScaleX10ToInt32(a.Value)));
                        }
                    }
                    else if (metric.Metric == "memory")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        memEntries = new List<(int, int)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            memEntries.Add((_names.RegisterOrGet(a.Name), ScaleX10ToInt32(a.Value)));
                        }
                    }
                    else if (metric.Metric == "storage")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        storageEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            storageEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric == "net")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        netEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            netEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric == "storage-read")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        storageReadEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            storageReadEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric == "storage-write")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        storageWriteEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            storageWriteEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric == "net-down")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        netDownEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            netDownEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric == "net-up")
                    {
                        if (metric.Apps.Count == 0)
                        {
                            continue;
                        }
                        netUpEntries = new List<(int, long)>(metric.Apps.Count);
                        foreach (var a in metric.Apps)
                        {
                            netUpEntries.Add((_names.RegisterOrGet(a.Name), ScaleWholeToInt64(a.Value)));
                        }
                    }
                    else if (metric.Metric.StartsWith("gpu:", StringComparison.Ordinal))
                    {
                        // A gid with no scalar-side registration this flush
                        // has no app rows either, since both come from the
                        // same sensors.GetGpus() read.
                        if (metric.Apps.Count == 0 || _resolveGpuIndex(metric.Metric[4..]) is not { } gpuIndex)
                        {
                            continue;
                        }
                        gpuEntries ??= new List<(int, int, short, int)>();
                        foreach (var a in metric.Apps)
                        {
                            gpuEntries.Add((_names.RegisterOrGet(a.Name), gpuIndex, FixedPointCodec.ScaleX10(a.Value), ScaleVramMb(a.VramMb)));
                        }
                    }
                    else if (metric.Metric.StartsWith("vram:", StringComparison.Ordinal))
                    {
                        if (metric.Apps.Count == 0 || _resolveGpuIndex(metric.Metric[5..]) is not { } gpuIndex)
                        {
                            continue;
                        }
                        vramEntries ??= new List<(int, int, int)>();
                        foreach (var a in metric.Apps)
                        {
                            vramEntries.Add((_names.RegisterOrGet(a.Name), gpuIndex, RoundToMb(a.Value)));
                        }
                    }
                }

                if (cpuEntries is { Count: > 0 })
                {
                    AddPending(cpuByDay, day, tick.TsSec, cpuEntries);
                }
                if (memEntries is { Count: > 0 })
                {
                    AddPending(memByDay, day, tick.TsSec, memEntries);
                }
                if (gpuEntries is { Count: > 0 })
                {
                    AddPending(gpuByDay, day, tick.TsSec, gpuEntries);
                }
                if (vramEntries is { Count: > 0 })
                {
                    AddPending(vramByDay, day, tick.TsSec, vramEntries);
                }
                if (storageEntries is { Count: > 0 })
                {
                    AddPending(storageByDay, day, tick.TsSec, storageEntries);
                }
                if (netEntries is { Count: > 0 })
                {
                    AddPending(netByDay, day, tick.TsSec, netEntries);
                }
                if (storageReadEntries is { Count: > 0 })
                {
                    AddPending(storageReadByDay, day, tick.TsSec, storageReadEntries);
                }
                if (storageWriteEntries is { Count: > 0 })
                {
                    AddPending(storageWriteByDay, day, tick.TsSec, storageWriteEntries);
                }
                if (netDownEntries is { Count: > 0 })
                {
                    AddPending(netDownByDay, day, tick.TsSec, netDownEntries);
                }
                if (netUpEntries is { Count: > 0 })
                {
                    AddPending(netUpByDay, day, tick.TsSec, netUpEntries);
                }
            }

            foreach (var (day, dayTicks) in cpuByDay)
            {
                WriteSimpleDay(AppMetricKind.Cpu, day, dayTicks);
            }
            foreach (var (day, dayTicks) in memByDay)
            {
                WriteSimpleDay(AppMetricKind.Mem, day, dayTicks);
            }
            foreach (var (day, dayTicks) in gpuByDay)
            {
                WriteGpuDay(day, dayTicks);
            }
            foreach (var (day, dayTicks) in vramByDay)
            {
                WriteVramDay(day, dayTicks);
            }
            foreach (var (day, dayTicks) in storageByDay)
            {
                WriteStorageDay(AppMetricKind.Storage, day, dayTicks);
            }
            foreach (var (day, dayTicks) in netByDay)
            {
                WriteStorageDay(AppMetricKind.Net, day, dayTicks);
            }
            foreach (var (day, dayTicks) in storageReadByDay)
            {
                WriteStorageDay(AppMetricKind.StorageRead, day, dayTicks);
            }
            foreach (var (day, dayTicks) in storageWriteByDay)
            {
                WriteStorageDay(AppMetricKind.StorageWrite, day, dayTicks);
            }
            foreach (var (day, dayTicks) in netDownByDay)
            {
                WriteStorageDay(AppMetricKind.NetDown, day, dayTicks);
            }
            foreach (var (day, dayTicks) in netUpByDay)
            {
                WriteStorageDay(AppMetricKind.NetUp, day, dayTicks);
            }
        }

        if (pruneCutoffSec is { } cutoff)
        {
            var newFloor = Math.Max(PruneFloorSec, cutoff);
            if (newFloor != PruneFloorSec)
            {
                PruneFloorSec = newFloor;
                PersistPruneFloor(newFloor);
            }
            DeleteDaysBefore(FloorToDay(cutoff));
        }
    }

    private static void AddPending<T>(Dictionary<long, List<(long Ts, List<T> Apps)>> byDay, long day, long ts, List<T> apps)
    {
        if (!byDay.TryGetValue(day, out var list))
        {
            list = new List<(long, List<T>)>();
            byDay[day] = list;
        }
        list.Add((ts, apps));
    }

    private void DeleteDaysBefore(long cutoffDay)
    {
        foreach (var kind in AllKinds)
        {
            var dir = KindDir(kind);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(dir, "*.seg"))
            {
                if (ParseDay(file) is { } day && day < cutoffDay)
                {
                    TryDelete(file);
                    TryDelete(Path.ChangeExtension(file, ".ids"));
                }
            }

            // A crash between idMap.Flush() and the .seg FileStream being
            // created (WriteSimpleDay/WriteGpuDay/WriteVramDay) can leave an
            // .ids file with no matching .seg - dead weight regardless of
            // its day, since ReadKindDay only ever opens an .ids file after
            // finding its .seg first. Swept here rather than given its own
            // pass, since this already runs once per prune.
            foreach (var idsFile in Directory.GetFiles(dir, "*.ids"))
            {
                if (!File.Exists(Path.ChangeExtension(idsFile, ".seg")))
                {
                    TryDelete(idsFile);
                }
            }
        }
    }

    private static long? ParseDay(string segPath) =>
        long.TryParse(Path.GetFileNameWithoutExtension(segPath), out var day) ? day : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // ----- write: cpu/memory (identical shape, different directory) -----

    private void WriteSimpleDay(AppMetricKind kind, long day, List<(long Ts, List<(int GlobalId, int ValueX10)> Apps)> ticks)
    {
        var idMap = AppLocalIdMap.LoadForWrite(IdsPath(kind, day));
        var resolvedTicks = new List<(long Ts, List<(ushort LocalId, int ValueX10)> Apps)>(ticks.Count);
        foreach (var (ts, apps) in ticks)
        {
            var resolved = new List<(ushort, int)>(apps.Count);
            foreach (var (globalId, valueX10) in apps)
            {
                if (idMap.GetOrAdd(globalId) is { } localId)
                {
                    resolved.Add(((ushort)localId, valueX10));
                }
            }
            if (resolved.Count > 0)
            {
                resolvedTicks.Add((ts, resolved));
            }
        }

        // The id map must reach disk before the segment that references its
        // local ids - see AppLocalIdMap's class doc for why this order keeps
        // a crash between the two safe (an orphaned surplus id, never a
        // segment record pointing past the map).
        idMap.Flush();

        var segPath = SegPath(kind, day);
        TruncateTornTail(segPath, SimpleRecordWidth);
        using var fs = new FileStream(segPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        foreach (var (ts, apps) in resolvedTicks)
        {
            WriteSimpleTick(fs, ts, apps);
        }
        fs.Flush(flushToDisk: true);
    }

    private static void WriteSimpleTick(FileStream fs, long ts, List<(ushort LocalId, int ValueX10)> apps)
    {
        const int RecordWidth = SimpleRecordWidth;
        var bodyLength = 10 + apps.Count * RecordWidth;
        var buf = new byte[bodyLength + 4];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), ts);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)apps.Count);

        var pos = 10;
        foreach (var (localId, valueX10) in apps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), localId);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 2, 4), valueX10);
            pos += RecordWidth;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static List<(long Ts, List<AppEntry> Apps)> ReadSimpleDay(string path, int[] idMap)
    {
        const int RecordWidth = SimpleRecordWidth;
        var result = new List<(long, List<AppEntry>)>();
        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        while (pos + 10 <= bytes.Length)
        {
            var ts = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos, 8));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 8, 2));
            var bodyLength = 10 + count * RecordWidth;
            if (pos + bodyLength + 4 > bytes.Length)
            {
                break;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + bodyLength, 4));
            if (Crc32.Compute(bytes.AsSpan(pos, bodyLength)) != expectedCrc)
            {
                break;
            }

            var apps = new List<AppEntry>(count);
            var entryPos = pos + 10;
            for (var i = 0; i < count; i++)
            {
                var localId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos, 2));
                var valueX10 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entryPos + 2, 4));
                if (localId < idMap.Length)
                {
                    apps.Add(new AppEntry(idMap[localId], -1, valueX10 / 10.0, null));
                }
                entryPos += RecordWidth;
            }
            result.Add((ts, apps));
            pos += bodyLength + 4;
        }
        return result;
    }

    // ----- write/read: gpu -----

    private void WriteGpuDay(long day, List<(long Ts, List<(int GlobalId, int GpuIndex, short LoadX10, int VramMbOrSentinel)> Apps)> ticks)
    {
        var idMap = AppLocalIdMap.LoadForWrite(IdsPath(AppMetricKind.Gpu, day));
        var resolvedTicks = new List<(long Ts, List<(ushort LocalId, int GpuIndex, short LoadX10, int VramMbOrSentinel)> Apps)>(ticks.Count);
        foreach (var (ts, apps) in ticks)
        {
            var resolved = new List<(ushort, int, short, int)>(apps.Count);
            foreach (var (globalId, gpuIndex, loadX10, vram) in apps)
            {
                if (idMap.GetOrAdd(globalId) is { } localId)
                {
                    resolved.Add(((ushort)localId, gpuIndex, loadX10, vram));
                }
            }
            if (resolved.Count > 0)
            {
                resolvedTicks.Add((ts, resolved));
            }
        }

        idMap.Flush();

        var segPath = SegPath(AppMetricKind.Gpu, day);
        TruncateTornTail(segPath, GpuRecordWidth);
        using var fs = new FileStream(segPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        foreach (var (ts, apps) in resolvedTicks)
        {
            WriteGpuTick(fs, ts, apps);
        }
        fs.Flush(flushToDisk: true);
    }

    private static void WriteGpuTick(FileStream fs, long ts, List<(ushort LocalId, int GpuIndex, short LoadX10, int VramMbOrSentinel)> apps)
    {
        const int RecordWidth = GpuRecordWidth;
        var bodyLength = 10 + apps.Count * RecordWidth;
        var buf = new byte[bodyLength + 4];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), ts);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)apps.Count);

        var pos = 10;
        foreach (var (localId, gpuIndex, loadX10, vram) in apps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), localId);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 2, 2), (ushort)gpuIndex);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(pos + 4, 2), loadX10);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 6, 4), vram);
            pos += RecordWidth;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static List<(long Ts, List<AppEntry> Apps)> ReadGpuDay(string path, int[] idMap)
    {
        const int RecordWidth = GpuRecordWidth;
        var result = new List<(long, List<AppEntry>)>();
        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        while (pos + 10 <= bytes.Length)
        {
            var ts = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos, 8));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 8, 2));
            var bodyLength = 10 + count * RecordWidth;
            if (pos + bodyLength + 4 > bytes.Length)
            {
                break;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + bodyLength, 4));
            if (Crc32.Compute(bytes.AsSpan(pos, bodyLength)) != expectedCrc)
            {
                break;
            }

            var apps = new List<AppEntry>(count);
            var entryPos = pos + 10;
            for (var i = 0; i < count; i++)
            {
                var localId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos, 2));
                var gpuIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos + 2, 2));
                var loadX10 = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(entryPos + 4, 2));
                var vramRaw = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entryPos + 6, 4));
                if (localId < idMap.Length)
                {
                    apps.Add(new AppEntry(idMap[localId], gpuIndex, FixedPointCodec.UnscaleX10(loadX10) ?? 0, UnscaleVramMb(vramRaw)));
                }
                entryPos += RecordWidth;
            }
            result.Add((ts, apps));
            pos += bodyLength + 4;
        }
        return result;
    }

    // ----- write/read: vram -----

    private void WriteVramDay(long day, List<(long Ts, List<(int GlobalId, int GpuIndex, int ValueMb)> Apps)> ticks)
    {
        var idMap = AppLocalIdMap.LoadForWrite(IdsPath(AppMetricKind.Vram, day));
        var resolvedTicks = new List<(long Ts, List<(ushort LocalId, int GpuIndex, int ValueMb)> Apps)>(ticks.Count);
        foreach (var (ts, apps) in ticks)
        {
            var resolved = new List<(ushort, int, int)>(apps.Count);
            foreach (var (globalId, gpuIndex, valueMb) in apps)
            {
                if (idMap.GetOrAdd(globalId) is { } localId)
                {
                    resolved.Add(((ushort)localId, gpuIndex, valueMb));
                }
            }
            if (resolved.Count > 0)
            {
                resolvedTicks.Add((ts, resolved));
            }
        }

        idMap.Flush();

        var segPath = SegPath(AppMetricKind.Vram, day);
        TruncateTornTail(segPath, VramRecordWidth);
        using var fs = new FileStream(segPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        foreach (var (ts, apps) in resolvedTicks)
        {
            WriteVramTick(fs, ts, apps);
        }
        fs.Flush(flushToDisk: true);
    }

    private static void WriteVramTick(FileStream fs, long ts, List<(ushort LocalId, int GpuIndex, int ValueMb)> apps)
    {
        const int RecordWidth = VramRecordWidth;
        var bodyLength = 10 + apps.Count * RecordWidth;
        var buf = new byte[bodyLength + 4];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), ts);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)apps.Count);

        var pos = 10;
        foreach (var (localId, gpuIndex, valueMb) in apps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), localId);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 2, 2), (ushort)gpuIndex);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 4, 4), valueMb);
            pos += RecordWidth;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static List<(long Ts, List<AppEntry> Apps)> ReadVramDay(string path, int[] idMap)
    {
        const int RecordWidth = VramRecordWidth;
        var result = new List<(long, List<AppEntry>)>();
        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        while (pos + 10 <= bytes.Length)
        {
            var ts = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos, 8));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 8, 2));
            var bodyLength = 10 + count * RecordWidth;
            if (pos + bodyLength + 4 > bytes.Length)
            {
                break;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + bodyLength, 4));
            if (Crc32.Compute(bytes.AsSpan(pos, bodyLength)) != expectedCrc)
            {
                break;
            }

            var apps = new List<AppEntry>(count);
            var entryPos = pos + 10;
            for (var i = 0; i < count; i++)
            {
                var localId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos, 2));
                var gpuIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos + 2, 2));
                var valueMb = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entryPos + 4, 4));
                if (localId < idMap.Length)
                {
                    apps.Add(new AppEntry(idMap[localId], gpuIndex, valueMb, null));
                }
                entryPos += RecordWidth;
            }
            result.Add((ts, apps));
            pos += bodyLength + 4;
        }
        return result;
    }

    // ----- write/read: storage, net (identical shape, different directory) -----

    private void WriteStorageDay(AppMetricKind kind, long day, List<(long Ts, List<(int GlobalId, long ValueBps)> Apps)> ticks)
    {
        var idMap = AppLocalIdMap.LoadForWrite(IdsPath(kind, day));
        var resolvedTicks = new List<(long Ts, List<(ushort LocalId, long ValueBps)> Apps)>(ticks.Count);
        foreach (var (ts, apps) in ticks)
        {
            var resolved = new List<(ushort, long)>(apps.Count);
            foreach (var (globalId, valueBps) in apps)
            {
                if (idMap.GetOrAdd(globalId) is { } localId)
                {
                    resolved.Add(((ushort)localId, valueBps));
                }
            }
            if (resolved.Count > 0)
            {
                resolvedTicks.Add((ts, resolved));
            }
        }

        idMap.Flush();

        var segPath = SegPath(kind, day);
        TruncateTornTail(segPath, StorageRecordWidth);
        using var fs = new FileStream(segPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        foreach (var (ts, apps) in resolvedTicks)
        {
            WriteStorageTick(fs, ts, apps);
        }
        fs.Flush(flushToDisk: true);
    }

    private static void WriteStorageTick(FileStream fs, long ts, List<(ushort LocalId, long ValueBps)> apps)
    {
        const int RecordWidth = StorageRecordWidth;
        var bodyLength = 10 + apps.Count * RecordWidth;
        var buf = new byte[bodyLength + 4];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), ts);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)apps.Count);

        var pos = 10;
        foreach (var (localId, valueBps) in apps)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), localId);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos + 2, 8), valueBps);
            pos += RecordWidth;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static List<(long Ts, List<AppEntry> Apps)> ReadStorageDay(string path, int[] idMap)
    {
        const int RecordWidth = StorageRecordWidth;
        var result = new List<(long, List<AppEntry>)>();
        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        while (pos + 10 <= bytes.Length)
        {
            var ts = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos, 8));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 8, 2));
            var bodyLength = 10 + count * RecordWidth;
            if (pos + bodyLength + 4 > bytes.Length)
            {
                break;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + bodyLength, 4));
            if (Crc32.Compute(bytes.AsSpan(pos, bodyLength)) != expectedCrc)
            {
                break;
            }

            var apps = new List<AppEntry>(count);
            var entryPos = pos + 10;
            for (var i = 0; i < count; i++)
            {
                var localId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entryPos, 2));
                var valueBps = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entryPos + 2, 8));
                if (localId < idMap.Length)
                {
                    apps.Add(new AppEntry(idMap[localId], -1, valueBps, null));
                }
                entryPos += RecordWidth;
            }
            result.Add((ts, apps));
            pos += bodyLength + 4;
        }
        return result;
    }

    // A crash mid-append can leave a torn trailing tick record in a day
    // segment; unlike AppNameDictionary/AppLocalIdMap/EntityRegistry, this
    // format has no long-lived writer handle to truncate the file at load
    // time, so each Write*Day call truncates immediately before it appends -
    // otherwise the new records would land after unreachable garbage and
    // every future ReadKindDay would stop at that garbage, never reaching
    // them (see ComputeCleanLength for the shared bounds/Crc walk every
    // Read*Day method above already performs).
    private static void TruncateTornTail(string segPath, int recordWidth)
    {
        if (!File.Exists(segPath))
        {
            return;
        }

        var bytes = File.ReadAllBytes(segPath);
        var cleanLength = ComputeCleanLength(bytes, recordWidth);
        if (cleanLength != bytes.Length)
        {
            using var fs = new FileStream(segPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(cleanLength);
        }
    }

    private static long ComputeCleanLength(byte[] bytes, int recordWidth)
    {
        var pos = 0;
        while (pos + 10 <= bytes.Length)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 8, 2));
            var bodyLength = 10 + count * recordWidth;
            if (pos + bodyLength + 4 > bytes.Length)
            {
                break;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + bodyLength, 4));
            if (Crc32.Compute(bytes.AsSpan(pos, bodyLength)) != expectedCrc)
            {
                break;
            }
            pos += bodyLength + 4;
        }
        return pos;
    }

    private List<(long Ts, List<AppEntry> Apps)> ReadKindDay(AppMetricKind kind, long day)
    {
        var segPath = SegPath(kind, day);
        try
        {
            if (!File.Exists(segPath))
            {
                return new List<(long, List<AppEntry>)>();
            }
            var idMap = AppLocalIdMap.ReadOnly(IdsPath(kind, day));
            return kind switch
            {
                AppMetricKind.Cpu or AppMetricKind.Mem => ReadSimpleDay(segPath, idMap),
                AppMetricKind.Gpu => ReadGpuDay(segPath, idMap),
                AppMetricKind.Vram => ReadVramDay(segPath, idMap),
                AppMetricKind.Storage or AppMetricKind.Net or AppMetricKind.StorageRead or AppMetricKind.StorageWrite
                    or AppMetricKind.NetDown or AppMetricKind.NetUp => ReadStorageDay(segPath, idMap),
                _ => new List<(long, List<AppEntry>)>(),
            };
        }
        catch (IOException)
        {
            // A concurrent prune deleted this day's files between the
            // existence check above and the read - treat it the same as the
            // day never having existed.
            return new List<(long, List<AppEntry>)>();
        }
    }

    private IEnumerable<long> ExistingDays(AppMetricKind kind)
    {
        var dir = KindDir(kind);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<long>();
        }
        var days = Directory.GetFiles(dir, "*.seg")
            .Select(ParseDay)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();
        days.Sort();
        return days;
    }

    // Every existing day file for kind whose day number falls in
    // [FloorToDay(fromSec), FloorToDay(toSec)] - NOT every day number in
    // that range: a caller window can be (and routinely is, e.g. a query
    // with toSec=long.MaxValue meaning "everything") far wider than any data
    // that exists, and walking every day number in between would iterate
    // effectively forever. Bounding by ExistingDays keeps the walk to
    // however many days this kind actually has on disk, capped by
    // MetricsHistory.RetentionDays regardless of how wide fromSec/toSec are.
    private IEnumerable<long> DaysToScan(AppMetricKind kind, long fromSec, long toSec)
    {
        var firstDay = FloorToDay(fromSec);
        var lastDay = FloorToDay(toSec);
        return ExistingDays(kind).Where(d => d >= firstDay && d <= lastDay);
    }

    // "cpu"/"memory" resolve directly; "gpu"/"vram" (bare, no adapter id)
    // resolve with a null filter so the caller aggregates across every
    // adapter instead of one; "gpu:<gid>"/"vram:<gid>" resolve through
    // resolveGpuIndex - a gid it has never seen yields no kind at all, not
    // an error.
    private (AppMetricKind? Kind, int? GpuIndexFilter) ResolveMetric(string metric)
    {
        if (metric == "cpu")
        {
            return (AppMetricKind.Cpu, null);
        }
        if (metric == "memory")
        {
            return (AppMetricKind.Mem, null);
        }
        if (metric == "storage")
        {
            return (AppMetricKind.Storage, null);
        }
        if (metric == "net")
        {
            return (AppMetricKind.Net, null);
        }
        if (metric == "storage-read")
        {
            return (AppMetricKind.StorageRead, null);
        }
        if (metric == "storage-write")
        {
            return (AppMetricKind.StorageWrite, null);
        }
        if (metric == "net-down")
        {
            return (AppMetricKind.NetDown, null);
        }
        if (metric == "net-up")
        {
            return (AppMetricKind.NetUp, null);
        }
        if (metric == "gpu")
        {
            return (AppMetricKind.Gpu, null);
        }
        if (metric.StartsWith("gpu:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[4..]) is { } idx ? (AppMetricKind.Gpu, idx) : (null, null);
        }
        if (metric == "vram")
        {
            return (AppMetricKind.Vram, null);
        }
        if (metric.StartsWith("vram:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[5..]) is { } idx ? (AppMetricKind.Vram, idx) : (null, null);
        }
        return (null, null);
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps)
    {
        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind)
        {
            return Array.Empty<AppWindowStat>();
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return Array.Empty<AppWindowStat>();
        }

        var sum = new Dictionary<int, double>();
        var max = new Dictionary<int, double>();
        var sampledTicks = new HashSet<long>();

        foreach (var day in DaysToScan(kind, effectiveFrom, toSec))
        {
            foreach (var (ts, apps) in ReadKindDay(kind, day))
            {
                if (ts < effectiveFrom || ts > toSec)
                {
                    continue;
                }

                // app_gpu_seconds/app_vram_seconds carry a gpu dimension: a
                // bare query (gpuFilter null) can have more than one
                // adapter's entry per (app, ts) - pre-aggregating here
                // collapses those to one summed value before ranking, so the
                // eventual max reflects the combined-adapter peak in a
                // single tick, matching QueryTopApps' SQL isMultiAdapter
                // pre-aggregation subquery.
                var perTick = new Dictionary<int, double>();
                foreach (var a in apps)
                {
                    if (gpuFilter is { } gf && a.GpuIndex != gf)
                    {
                        continue;
                    }
                    perTick[a.GlobalId] = perTick.TryGetValue(a.GlobalId, out var v) ? v + a.Value : a.Value;
                }
                if (perTick.Count == 0)
                {
                    continue;
                }

                sampledTicks.Add(ts);
                foreach (var (globalId, v) in perTick)
                {
                    sum[globalId] = sum.TryGetValue(globalId, out var s) ? s + v : v;
                    max[globalId] = max.TryGetValue(globalId, out var m) ? Math.Max(m, v) : v;
                }
            }
        }

        var expectedTicks = sampledTicks.Count;
        if (expectedTicks == 0)
        {
            return Array.Empty<AppWindowStat>();
        }

        return sum
            .Select(kv => new AppWindowStat(_names.GetName(kv.Key), kv.Value / expectedTicks, max[kv.Key]))
            .OrderByDescending(a => a.Avg)
            .Take(maxApps)
            .ToList();
    }

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec)
    {
        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind || _names.TryGetId(appName) is not { } globalId)
        {
            return Array.Empty<AppRawPoint>();
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return Array.Empty<AppRawPoint>();
        }

        var points = new List<AppRawPoint>();
        foreach (var day in DaysToScan(kind, effectiveFrom, toSec))
        {
            foreach (var (ts, apps) in ReadKindDay(kind, day))
            {
                if (ts < effectiveFrom || ts > toSec)
                {
                    continue;
                }

                double? valueSum = null;
                double? vramSum = null;
                var any = false;
                foreach (var a in apps)
                {
                    if (a.GlobalId != globalId || (gpuFilter is { } gf && a.GpuIndex != gf))
                    {
                        continue;
                    }
                    any = true;
                    valueSum = (valueSum ?? 0) + a.Value;
                    vramSum = MetricsHistory.SumNullable(vramSum, a.VramMb);
                }
                if (any)
                {
                    points.Add(new AppRawPoint(ts, valueSum, vramSum));
                }
            }
        }

        points.Sort((a, b) => a.TsSec.CompareTo(b.TsSec));
        return points;
    }

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec)
    {
        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind)
        {
            return Array.Empty<long>();
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return Array.Empty<long>();
        }

        var result = new SortedSet<long>();
        foreach (var day in DaysToScan(kind, effectiveFrom, toSec))
        {
            foreach (var (ts, apps) in ReadKindDay(kind, day))
            {
                if (ts < effectiveFrom || ts > toSec)
                {
                    continue;
                }
                var matches = gpuFilter is { } gf ? apps.Any(a => a.GpuIndex == gf) : apps.Count > 0;
                if (matches)
                {
                    result.Add(ts);
                }
            }
        }
        return result.ToList();
    }

    public long? QueryFirstSeen(string appName)
    {
        if (_names.TryGetId(appName) is not { } globalId)
        {
            return null;
        }

        long? earliest = null;
        foreach (var kind in AllKinds)
        {
            foreach (var day in ExistingDays(kind))
            {
                long? dayMin = null;
                foreach (var (ts, apps) in ReadKindDay(kind, day))
                {
                    if (ts < PruneFloorSec || !apps.Any(a => a.GlobalId == globalId))
                    {
                        continue;
                    }
                    dayMin = dayMin is { } m ? Math.Min(m, ts) : ts;
                }
                if (dayMin is { } found)
                {
                    earliest = earliest is { } e ? Math.Min(e, found) : found;
                    break; // days are ascending: a later day can't be earlier.
                }
            }
        }
        return earliest;
    }

    private static int ScaleX10ToInt32(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var scaled = Math.Round(value * 10);
        if (scaled <= int.MinValue)
        {
            return int.MinValue + 1;
        }
        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)scaled;
    }

    private static long ScaleWholeToInt64(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var rounded = Math.Round(value);
        if (rounded <= long.MinValue)
        {
            return long.MinValue + 1;
        }
        if (rounded >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)rounded;
    }

    private static int RoundToMb(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var rounded = Math.Round(value);
        if (rounded <= int.MinValue)
        {
            return int.MinValue + 1;
        }
        if (rounded >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)rounded;
    }

    private static int ScaleVramMb(double? value) => value is { } v ? RoundToMb(v) : int.MinValue;

    private static double? UnscaleVramMb(int raw) => raw == int.MinValue ? null : raw;

    private void PersistPruneFloor(long floor)
    {
        // Write-then-atomic-rename rather than an in-place write: File.Move
        // with overwrite is an atomic rename on both Windows and POSIX, so a
        // crash mid-write only ever leaves the OLD complete value in place,
        // never a torn one.
        var tmp = _pruneFloorPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buf, floor);
            fs.Write(buf);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _pruneFloorPath, overwrite: true);
    }

    private static long LoadPruneFloor(string path)
    {
        if (!File.Exists(path))
        {
            return RingFile.UnwrittenStamp;
        }
        var bytes = File.ReadAllBytes(path);
        return bytes.Length == 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : RingFile.UnwrittenStamp;
    }

    public void Dispose() => _names.Dispose();
}
