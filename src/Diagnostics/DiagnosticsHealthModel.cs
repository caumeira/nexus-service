using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics;

/// <summary>Wire status values shared by the overall health value and every component's status.</summary>
public static class HealthStatuses
{
    public const string Ok = "ok";
    public const string Watch = "watch";
    public const string Act = "act";
    public const string Unknown = "unknown";
}

public sealed record DiagnosticsHealthResponse
{
    public DateTime GeneratedAt { get; init; }
    public bool Supported { get; init; }
    public string Overall { get; init; } = HealthStatuses.Unknown;
    public IReadOnlyList<HealthComponent> Components { get; init; } = Array.Empty<HealthComponent>();
}

public sealed record HealthComponent
{
    /// <summary>kind:stableKey, e.g. "storage:S6Z1NX0T123456".</summary>
    public string Id { get; init; } = "";
    /// <summary>storage | memory | gpu | cooling | system.</summary>
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = HealthStatuses.Unknown;
    public IReadOnlyList<HealthComponentReason> Reasons { get; init; } = Array.Empty<HealthComponentReason>();
}

public sealed record HealthComponentReason(string Code, string Severity, string Summary, string Detail);

/// <summary>
/// Aggregates every diagnostics module into the GET /diagnostics/health payload.
/// <see cref="Compute"/> is a pure function of plain snapshot DTOs so it is
/// testable on any platform without the Windows-only monitor classes;
/// <see cref="BuildHealth"/> is the DI-facing instance wrapper that gathers
/// those snapshots and caches the result for 30s (the health page and the
/// alert service both poll through this).
/// </summary>
public sealed class DiagnosticsHealthModel
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventWindow = TimeSpan.FromDays(30);

    private readonly SmartHealthMonitor _smart;
    private readonly CoolingStallDetector _cooling;
    private readonly GpuHealthMonitor _gpu;
    private readonly EventLogMonitor _events;
    private readonly MemoryDiagnosticOrchestrator _memDiag;
    private readonly PnpProblemScanner _pnp;
    private readonly ISensorProvider _sensors;

    private readonly object _gate = new();
    private DiagnosticsHealthResponse? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public DiagnosticsHealthModel(
        SmartHealthMonitor smart,
        CoolingStallDetector cooling,
        GpuHealthMonitor gpu,
        EventLogMonitor events,
        MemoryDiagnosticOrchestrator memDiag,
        PnpProblemScanner pnp,
        ISensorProvider sensors)
    {
        _smart = smart;
        _cooling = cooling;
        _gpu = gpu;
        _events = events;
        _memDiag = memDiag;
        _pnp = pnp;
        _sensors = sensors;
    }

    /// <summary>forceRefresh bypasses this model's own cache; module caches are
    /// unaffected - each module snapshot still goes through its normal
    /// Snapshot() call.</summary>
    public DiagnosticsHealthResponse BuildHealth(bool forceRefresh = false)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!forceRefresh && _cached is not null && now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }

            var result = Compute(
                smart: _smart.Snapshot(),
                cooling: _cooling.Snapshot(),
                gpu: _gpu.Snapshot(),
                counts30d: _events.CountsSince(EventWindow),
                lastMemoryTest: _memDiag.LastResult(),
                pnp: _pnp.Snapshot(),
                knownGpuModels: _sensors.GetGpuModels(),
                windowsSupported: OperatingSystem.IsWindows(),
                generatedAtUtc: now);

            _cached = result;
            _cachedAtUtc = now;
            return result;
        }
    }

    /// <summary>
    /// Pure aggregation: every module's own Windows-gating already collapses to
    /// unsupported/empty data on non-Windows, so the only extra gate needed
    /// here is skipping storage/gpu/memory/system entirely when
    /// <paramref name="windowsSupported"/> is false - cooling stays evaluated
    /// on every platform (IFanControlProvider exists cross-platform).
    ///
    /// Every component reflects current state only: SMART classification,
    /// active GPU throttle, last memory test result, live pnp problems, and
    /// cooling stall detection. 30-day event history (TDRs, driver errors,
    /// bugchecks, dirty shutdowns, WHEA) never feeds a status here - it stays
    /// on GET /diagnostics/system and /diagnostics/gpu and the PDF report's
    /// stability grid, both informational-only. <paramref name="counts30d"/>
    /// is accepted so the "history never affects status" invariant is testable
    /// by construction; nothing in this method reads it.
    /// </summary>
    public static DiagnosticsHealthResponse Compute(
        SmartSnapshot smart,
        CoolingStallSnapshot cooling,
        GpuHealthSnapshot gpu,
        IReadOnlyDictionary<string, int> counts30d,
        MemoryTestResult? lastMemoryTest,
        PnpProblemSnapshot pnp,
        IReadOnlyList<string> knownGpuModels,
        bool windowsSupported,
        DateTime generatedAtUtc)
    {
        var components = new List<HealthComponent>();

        if (windowsSupported)
        {
            AddStorageComponents(components, smart);
            AddGpuComponents(components, gpu, knownGpuModels);
            AddMemoryComponent(components, lastMemoryTest);
            AddSystemComponent(components, pnp);
        }
        AddCoolingComponents(components, cooling);

        return new DiagnosticsHealthResponse
        {
            GeneratedAt = generatedAtUtc,
            Supported = windowsSupported,
            Overall = WorstStatus(components.Select(c => c.Status)),
            Components = components,
        };
    }

    private static void AddStorageComponents(List<HealthComponent> components, SmartSnapshot smart)
    {
        if (!smart.Supported)
        {
            return;
        }

        foreach (var drive in smart.Drives)
        {
            var reasons = drive.DetailedReasons
                .Select(r => new HealthComponentReason(r.Code, MapReasonSeverity(r.Severity), r.Summary, r.Detail))
                .ToList();
            components.Add(new HealthComponent
            {
                Id = drive.Id,
                Kind = "storage",
                Name = drive.Name,
                Status = MapDriveStatus(drive.Status),
                Reasons = reasons,
            });
        }
    }

    private static string MapDriveStatus(string status) => status switch
    {
        "good" => HealthStatuses.Ok,
        "caution" => HealthStatuses.Watch,
        "warning" => HealthStatuses.Act,
        "bad" => HealthStatuses.Act,
        _ => HealthStatuses.Unknown,
    };

    private static string MapReasonSeverity(ReasonSeverity severity) =>
        severity == ReasonSeverity.Act ? HealthStatuses.Act : HealthStatuses.Watch;

    private static void AddCoolingComponents(List<HealthComponent> components, CoolingStallSnapshot cooling)
    {
        if (cooling.Devices.Count == 0)
        {
            return;
        }

        foreach (var device in cooling.Devices)
        {
            if (device.Status != CoolingStallStatuses.Stalled && device.Status != CoolingStallStatuses.Suspect)
            {
                continue;
            }

            var isPump = string.Equals(device.Type, "pump", StringComparison.OrdinalIgnoreCase);
            var code = isPump ? "cooling.pumpStall" : "cooling.fanStall";
            var severity = device.Status == CoolingStallStatuses.Stalled ? HealthStatuses.Act : HealthStatuses.Watch;
            var summary = device.Status == CoolingStallStatuses.Stalled
                ? $"{device.Name} reports 0 RPM while driven"
                : $"{device.Name} reports 0 RPM at low duty";
            var detail = $"rpm={Fmt(device.Rpm)} targetDuty={Fmt(device.TargetDutyPercent)}% since={device.SinceUtc:O}";

            components.Add(new HealthComponent
            {
                Id = $"cooling:{device.Id}",
                Kind = "cooling",
                Name = device.Name,
                Status = severity,
                Reasons = new List<HealthComponentReason> { new(code, severity, summary, detail) },
            });
        }

        components.Add(new HealthComponent
        {
            Id = "cooling",
            Kind = "cooling",
            Name = $"Cooling ({cooling.Devices.Count})",
            Status = HealthStatuses.Ok,
            Reasons = Array.Empty<HealthComponentReason>(),
        });
    }

    private static string Fmt(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static void AddGpuComponents(
        List<HealthComponent> components,
        GpuHealthSnapshot gpu,
        IReadOnlyList<string> knownGpuModels)
    {
        if (gpu.Supported)
        {
            for (var i = 0; i < gpu.Gpus.Count; i++)
            {
                var info = gpu.Gpus[i];
                var reasons = BuildGpuReasons(info.Throttle);
                components.Add(new HealthComponent
                {
                    Id = $"gpu:{i}",
                    Kind = "gpu",
                    Name = info.Name,
                    Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
                    Reasons = reasons,
                });
            }
            return;
        }

        // No live NVML/ADL readout: only surface a placeholder component when a
        // GPU is actually known to exist (from sensor detection), with nothing
        // to say about its status.
        if (knownGpuModels.Count == 0)
        {
            return;
        }

        components.Add(new HealthComponent
        {
            Id = "gpu:0",
            Kind = "gpu",
            Name = knownGpuModels.FirstOrDefault() ?? "GPU",
            Status = HealthStatuses.Unknown,
            Reasons = Array.Empty<HealthComponentReason>(),
        });
    }

    private static List<HealthComponentReason> BuildGpuReasons(GpuThrottleInfo throttle)
    {
        var reasons = new List<HealthComponentReason>();

        if (throttle.Active.Contains("hwThermal") || throttle.Active.Contains("hwPowerBrake"))
        {
            reasons.Add(new HealthComponentReason("gpu.thermalThrottle", HealthStatuses.Watch,
                "GPU is hardware throttling",
                "The GPU reports an active hardware thermal or power-brake throttle, both driven by the board directly rather than software policy."));
        }

        return reasons;
    }

    private static void AddMemoryComponent(List<HealthComponent> components, MemoryTestResult? lastMemoryTest)
    {
        var reasons = new List<HealthComponentReason>();

        if (lastMemoryTest is { Result: MemoryTestResult.Failed })
        {
            reasons.Add(new HealthComponentReason("memory.testFailed", HealthStatuses.Act,
                "The last Windows Memory Diagnostic run reported errors",
                lastMemoryTest.Detail ?? "Microsoft-Windows-MemoryDiagnostics-Results logged a failed run."));
        }

        components.Add(new HealthComponent
        {
            Id = "memory",
            Kind = "memory",
            Name = "Memory",
            Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
            Reasons = reasons,
        });
    }

    private static void AddSystemComponent(List<HealthComponent> components, PnpProblemSnapshot pnp)
    {
        var reasons = new List<HealthComponentReason>();

        if (pnp.Devices.Count >= 1)
        {
            reasons.Add(new HealthComponentReason("system.pnpProblems", HealthStatuses.Watch,
                $"{pnp.Devices.Count} device(s) reporting a Device Manager problem",
                "Windows Device Manager reports a non-zero ConfigManagerErrorCode for at least one device."));
        }

        components.Add(new HealthComponent
        {
            Id = "system",
            Kind = "system",
            Name = "System",
            Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
            Reasons = reasons,
        });
    }

    private static string WorstReasonStatus(IReadOnlyList<HealthComponentReason> reasons, string baseline)
    {
        if (reasons.Any(r => r.Severity == HealthStatuses.Act))
        {
            return HealthStatuses.Act;
        }
        if (reasons.Any(r => r.Severity == HealthStatuses.Watch))
        {
            return HealthStatuses.Watch;
        }
        return baseline;
    }

    private static string WorstStatus(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Contains(HealthStatuses.Act))
        {
            return HealthStatuses.Act;
        }
        if (list.Contains(HealthStatuses.Watch))
        {
            return HealthStatuses.Watch;
        }
        if (list.Contains(HealthStatuses.Unknown))
        {
            return HealthStatuses.Unknown;
        }
        return HealthStatuses.Ok;
    }
}
