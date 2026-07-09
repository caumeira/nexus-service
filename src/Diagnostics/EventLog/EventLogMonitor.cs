using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;
#endif

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>
/// Backfills, then live-tails, every DiagnosticEventCatalog source into a
/// capped in-memory incident store. Windows-only: on other platforms
/// ExecuteAsync no-ops and Snapshot/CountsSince stay empty, the same gating
/// pattern LibreHardwareSensorProvider's callers use.
/// </summary>
public sealed class EventLogMonitor : BackgroundService
{
    private const int MaxIncidents = 3000;

    private readonly object _lock = new();
    private readonly List<DiagnosticIncident> _incidents = new();
    private int _generation;

    public IReadOnlyList<DiagnosticIncident> Snapshot(int days)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
        lock (_lock)
        {
            return _incidents
                .Where(i => i.TimeUtc >= cutoff)
                .OrderByDescending(i => i.TimeUtc)
                .ToList();
        }
    }

    public Dictionary<string, int> CountsSince(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var incident in _incidents)
            {
                if (incident.TimeUtc < cutoff)
                {
                    continue;
                }
                result[incident.Source] = result.GetValueOrDefault(incident.Source) + 1;
            }
        }
        return result;
    }

    /// <summary>Clears the in-memory store and (Windows only) re-runs the
    /// catalog backfill from scratch, offloaded to a thread-pool thread so the
    /// caller (POST /diagnostics/events/clear, awaited while wevtutil wipes
    /// the underlying System/Application logs) is never blocked on native
    /// EvtQuery/EvtNext calls. _generation is bumped under _lock on every
    /// reset; the boot-time backfill and every reset's own backfill each
    /// capture the generation current when they start and pass it through to
    /// every add. An add is dropped, not written, if the generation has since
    /// moved on - so a still-running stale pass (the boot backfill racing a
    /// clear during startup, or an older clear racing a newer one) can never
    /// resurrect incidents a later reset already superseded, and two
    /// concurrent clears can never interleave into duplicates. The live
    /// EvtSubscribe callback (<see cref="OnEvent"/>) is unaffected - it always
    /// adds under whatever generation is current at delivery time, since it
    /// reports a genuinely new event, not a replay.</summary>
    public Task ResetAndBackfillAsync()
    {
        int generation;
        lock (_lock)
        {
            _generation++;
            generation = _generation;
            _incidents.Clear();
        }
#if WINDOWS
        return Task.Run(() =>
        {
            foreach (var entry in DiagnosticEventCatalog.Entries)
            {
                Backfill(entry, generation);
            }
        });
#else
        return Task.CompletedTask;
#endif
    }

    private void AddIncident(DiagnosticIncident incident)
    {
        lock (_lock)
        {
            AddIncidentLocked(incident);
        }
    }

    // Returns false without adding if a later reset has bumped the generation
    // past what the caller's backfill pass started with. Internal (not
    // private) so EventLogMonitorTests can exercise the generation gate
    // directly - Backfill itself is Windows-only and needs live WevtApi state.
    internal bool TryAddIncident(DiagnosticIncident incident, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return false;
            }
            AddIncidentLocked(incident);
            return true;
        }
    }

    // Caller holds _lock.
    private void AddIncidentLocked(DiagnosticIncident incident)
    {
        _incidents.Add(incident);
        if (_incidents.Count > MaxIncidents)
        {
            Trim();
        }
    }

    // Caller holds _lock. Drops the oldest info-level incidents first, then
    // the oldest of any severity, so a burst of noisy transient events can't
    // push a critical incident out of the capped window.
    private void Trim()
    {
        var overflow = _incidents.Count - MaxIncidents;
        if (overflow <= 0)
        {
            return;
        }
        _incidents.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        var removed = 0;
        for (var i = 0; i < _incidents.Count && removed < overflow;)
        {
            if (_incidents[i].Severity == DiagnosticSeverity.Info)
            {
                _incidents.RemoveAt(i);
                removed++;
            }
            else
            {
                i++;
            }
        }
        while (removed < overflow && _incidents.Count > 0)
        {
            _incidents.RemoveAt(0);
            removed++;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
#if WINDOWS
        return RunWindowsAsync(stoppingToken);
#else
        return Task.CompletedTask;
#endif
    }

#if WINDOWS
    private const int BackfillCapPerSource = 300;
    private const long BackfillWindowMs = 2_592_000_000;

    private async Task RunWindowsAsync(CancellationToken stoppingToken)
    {
        // Hand control back to StartAsync immediately; backfill runs on a
        // thread-pool thread so /ping stays reachable during boot.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        int generation;
        lock (_lock)
        {
            generation = _generation;
        }

        var backfillCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in DiagnosticEventCatalog.Entries)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            backfillCounts[entry.Source] = Backfill(entry, generation);
        }
        var summary = string.Join(", ", backfillCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        ServiceLog.Info($"[event-log-monitor] backfill complete: {summary}");

        var subscriptions = new List<WevtApi.SafeEvtHandle>();
        var gcHandles = new List<GCHandle>();
        try
        {
            foreach (var group in DiagnosticEventCatalog.Entries.GroupBy(e => e.Channel, StringComparer.Ordinal))
            {
                var query = CombinedQuery(group);
                var state = new SubscriptionState(this, group.Key);
                var gcHandle = GCHandle.Alloc(state);
                gcHandles.Add(gcHandle);

                WevtApi.SafeEvtHandle sub;
                unsafe
                {
                    var callback = (IntPtr)(delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, uint>)&OnEvent;
                    sub = WevtApi.EvtSubscribe(IntPtr.Zero, IntPtr.Zero, group.Key, query,
                        IntPtr.Zero, GCHandle.ToIntPtr(gcHandle), callback, WevtApi.EvtSubscribeToFutureEvents);
                }
                if (sub.IsInvalid)
                {
                    ServiceLog.Warn($"[event-log-monitor] EvtSubscribe failed for channel {group.Key}: error {Marshal.GetLastWin32Error()}");
                    gcHandle.Free();
                    gcHandles.Remove(gcHandle);
                    continue;
                }
                subscriptions.Add(sub);
            }

            // Subscriptions deliver on native worker threads until closed;
            // block here so BackgroundService keeps the service alive and the
            // finally below runs on shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            // EvtClose on a subscription blocks until any in-flight callback
            // for it finishes, so every GCHandle it could reference is safe
            // to free right after.
            foreach (var sub in subscriptions)
            {
                sub.Dispose();
            }
            foreach (var gcHandle in gcHandles)
            {
                if (gcHandle.IsAllocated)
                {
                    gcHandle.Free();
                }
            }
        }
    }

    private static string CombinedQuery(IEnumerable<DiagnosticEventCatalogEntry> entries)
    {
        var predicates = entries.Select(e => $"({e.XPath})");
        return $"*[System[{string.Join(" or ", predicates)}]]";
    }

    private int Backfill(DiagnosticEventCatalogEntry entry, int generation)
    {
        var query = $"*[System[({entry.XPath}) and TimeCreated[timediff(@SystemTime) <= {BackfillWindowMs}]]]";
        using var resultSet = WevtApi.EvtQuery(IntPtr.Zero, entry.Channel, query,
            WevtApi.EvtQueryChannelPath | WevtApi.EvtQueryReverseDirection);
        if (resultSet.IsInvalid)
        {
            ServiceLog.Warn($"[event-log-monitor] backfill query failed for {entry.Source}: error {Marshal.GetLastWin32Error()}");
            return 0;
        }

        var count = 0;
        var buffer = new IntPtr[1];
        while (count < BackfillCapPerSource)
        {
            if (!WevtApi.EvtNext(resultSet, 1u, buffer, 0u, 0u, out var returned) || returned == 0)
            {
                break;
            }
            using var eventHandle = new WevtApi.SafeEvtHandle(buffer[0], ownsHandle: true);
            var xml = Render(eventHandle);
            var incident = xml is not null ? EventXmlParser.Parse(xml) : null;
            if (incident is not null && TryAddIncident(incident, generation))
            {
                count++;
            }
        }
        return count;
    }

    private static string? Render(WevtApi.SafeEvtHandle eventHandle)
    {
        // First call is expected to fail with ERROR_INSUFFICIENT_BUFFER; it
        // still reports the required buffer size (bytes, including the
        // trailing null) via `required`.
        _ = WevtApi.EvtRender(IntPtr.Zero, eventHandle, WevtApi.EvtRenderEventXml, 0, IntPtr.Zero, out var required, out _);
        if (required == 0)
        {
            return null;
        }
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!WevtApi.EvtRender(IntPtr.Zero, eventHandle, WevtApi.EvtRenderEventXml, required, buffer, out var used, out _))
            {
                return null;
            }
            var charCount = (int)(used / 2) - 1;
            return charCount > 0 ? Marshal.PtrToStringUni(buffer, charCount) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint OnEvent(int action, IntPtr userContext, IntPtr eventHandle)
    {
        SubscriptionState? state = null;
        try
        {
            if (userContext != IntPtr.Zero)
            {
                state = GCHandle.FromIntPtr(userContext).Target as SubscriptionState;
            }
            if (action != WevtApi.EvtSubscribeActionDeliver || state is null)
            {
                return 0;
            }
            using var handle = new WevtApi.SafeEvtHandle(eventHandle, ownsHandle: true);
            var xml = Render(handle);
            var incident = xml is not null ? EventXmlParser.Parse(xml) : null;
            if (incident is not null)
            {
                state.Monitor.AddIncident(incident);
            }
        }
        catch (Exception ex)
        {
            state?.WarnOnce(ex);
        }
        return 0;
    }

    private sealed class SubscriptionState
    {
        public EventLogMonitor Monitor { get; }
        private readonly string _channel;
        private int _warned;

        public SubscriptionState(EventLogMonitor monitor, string channel)
        {
            Monitor = monitor;
            _channel = channel;
        }

        public void WarnOnce(Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                ServiceLog.Warn($"[event-log-monitor] subscription callback error on channel {_channel}: {ex.Message}");
            }
        }
    }
#endif
}
