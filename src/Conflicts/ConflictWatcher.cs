using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Background service that polls the running process list every 5 s, matches
/// it against <see cref="ConflictAppCatalog.All"/>, and broadcasts the
/// current snapshot on the multiplex topic <c>conflicts</c> whenever the
/// detected set changes.
///
/// Scanning walks process names only and never queries CPU% / memory /
/// handles, so it runs regardless of subscriber count. Registers a snapshot
/// provider so newly-subscribing clients receive the current state without
/// waiting for the next change tick.
/// </summary>
public sealed class ConflictWatcher : BackgroundService
{
    public const string Topic = "conflicts";

    private readonly MultiplexHub _hub;
    private readonly OpenRgbProcessManager? _openRgb;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Last-published snapshot. Reads on broadcaster thread, also read on
    /// receive threads via <see cref="GetCachedSnapshotEnvelope"/>; the byte
    /// array is replaced atomically (volatile reference-assignment is atomic
    /// on every supported runtime).
    /// </summary>
    private volatile byte[]? _cachedEnvelope;

    /// <summary>Plain list mirror of the same snapshot for the REST endpoint.</summary>
    private volatile IReadOnlyList<DetectedConflict> _latest = Array.Empty<DetectedConflict>();

    /// <summary>Set of conflict ids currently detected. Used to detect changes.</summary>
    private string[] _lastDetectedIds = Array.Empty<string>();

    public ConflictWatcher(MultiplexHub hub, OpenRgbProcessManager? openRgb = null)
    {
        _hub = hub;
        _openRgb = openRgb;
        _hub.RegisterSnapshotProvider(Topic, GetCachedSnapshotEnvelope);
    }

    public IReadOnlyList<DetectedConflict> GetConflicts() => _latest;

    /// <summary>
    /// Resolve the catalog entry matching a given id (case-insensitive).
    /// Returns null when the id is unknown — used by ConflictRoutes to
    /// validate the kill payload before terminating anything.
    /// </summary>
    public static ConflictAppDefinition? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        foreach (var def in ConflictAppCatalog.All)
        {
            if (string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase))
                return def;
        }
        return null;
    }

    private ReadOnlyMemory<byte>? GetCachedSnapshotEnvelope()
    {
        var bytes = _cachedEnvelope;
        return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
    }

    public override void Dispose()
    {
        try
        { _hub.UnregisterSnapshotProvider(Topic); }
        catch { }
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the rest of the service finish boot — DI'd
        // dependencies (OpenRGB manager) may not have started their own work
        // yet, and a noisy first scan would race with the same processes we
        // are trying to ignore.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanAndPublish();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[conflict-watcher] scan failed: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void ScanAndPublish()
    {
        var detected = DetectRunningConflicts();

        // Quick equality check: ids only. Pids shift across reboots but we
        // don't need to republish every poll just because the OS recycled a
        // pid — only when the set of detected apps changes.
        var ids = new string[detected.Count];
        for (int i = 0; i < detected.Count; i++)
            ids[i] = detected[i].Id;
        Array.Sort(ids, StringComparer.Ordinal);

        bool changed = !ArraysEqual(ids, _lastDetectedIds);
        _lastDetectedIds = ids;
        _latest = detected;

        if (!changed && _cachedEnvelope is not null)
            return;

        var frame = new ConflictsFrame { Conflicts = detected.ToList() };
        var env = WsEnvelope.Build(Topic, frame, AppJsonContext.Default.ConflictsFrame);
        _cachedEnvelope = env.ToArray();

        if (_hub.TopicHasSubscribers(Topic))
        {
            _ = _hub.BroadcastTopicAsync(Topic, env);
        }
    }

    private List<DetectedConflict> DetectRunningConflicts()
    {
        var byName = BuildProcessNameIndex();
        if (byName.Count == 0)
            return new List<DetectedConflict>(0);

        var bundledOpenRgbPath = _openRgb?.ExePath;
        var result = new List<DetectedConflict>();

        foreach (var def in ConflictAppCatalog.All)
        {
            DetectedConflict? best = null;
            foreach (var procName in def.ProcessNames)
            {
                if (!byName.TryGetValue(procName, out var hits))
                    continue;

                foreach (var hit in hits)
                {
                    if (IsOurOwnOpenRgb(def.Id, hit.Pid, bundledOpenRgbPath))
                        continue;

                    // Keep the lowest pid as canonical for the catalog id so
                    // the SPA shows a deterministic value across renders.
                    if (best is null || hit.Pid < best.Pid)
                    {
                        best = new DetectedConflict
                        {
                            Id = def.Id,
                            DisplayName = def.DisplayName,
                            Category = def.Category,
                            ProcessName = procName,
                            Pid = hit.Pid,
                        };
                    }
                }
            }
            if (best is not null)
                result.Add(best);
        }
        return result;
    }

    /// <summary>
    /// Walk every running process exactly once and group by ProcessName so
    /// matching against the catalog is O(catalog × hits) instead of
    /// O(catalog × procs). OrdinalIgnoreCase matches how .NET casefolds
    /// Windows executable names.
    /// </summary>
    private static Dictionary<string, List<(int Pid, string? Path)>> BuildProcessNameIndex()
    {
        var index = new Dictionary<string, List<(int Pid, string? Path)>>(StringComparer.OrdinalIgnoreCase);

#if MACOS
        var pids = Platform.Mac.MacProcInfo.ListPids();
        if (pids.Length == 0)
            return index;
        var pathBuf = new byte[4096];
        foreach (var pid in pids)
        {
            if (pid <= 0)
                continue;
            var name = Platform.Mac.MacProcInfo.GetProcessName(pid, pathBuf);
            if (string.IsNullOrEmpty(name))
                continue;
            if (!index.TryGetValue(name, out var bucket))
            {
                bucket = new List<(int, string?)>(1);
                index[name] = bucket;
            }
            bucket.Add((pid, null));
        }
        return index;
#else
        // Windows + everything else: Process.GetProcesses() is the most
        // portable cross-platform path. Dispose every Process handle the
        // moment we have what we need so we don't accumulate kernel objects.
        var processes = Process.GetProcesses();
        foreach (var proc in processes)
        {
            try
            {
                string name;
                int pid;
                try
                {
                    name = proc.ProcessName;
                    pid = proc.Id;
                }
                catch { continue; }

                if (string.IsNullOrEmpty(name))
                    continue;
                if (!index.TryGetValue(name, out var bucket))
                {
                    bucket = new List<(int, string?)>(1);
                    index[name] = bucket;
                }
                bucket.Add((pid, null));
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return index;
#endif
    }

    /// <summary>
    /// Suppress matches against the bundled headless OpenRGB subprocess that
    /// the lighting stack spawns inside our install directory. Without this
    /// the warning would fire on every install that uses RGB.
    /// </summary>
    private static bool IsOurOwnOpenRgb(string defId, int pid, string? bundledPath)
    {
        if (!string.Equals(defId, "openrgb", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(bundledPath))
            return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var modulePath = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(modulePath))
                return false;
            return string.Equals(
                Path.GetFullPath(modulePath),
                Path.GetFullPath(bundledPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // MainModule access is denied for cross-session / elevated
            // processes. We can't prove the binary is ours, so let the
            // warning fire — even if the suspect is in fact our headless
            // OpenRGB.
            return false;
        }
    }

    private static bool ArraysEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
