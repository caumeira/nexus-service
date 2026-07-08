using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Lighting;

namespace Nexus.Service.Routes;

/// <summary>
/// Diagnostics app REST surface: aggregated health, per-domain detail, memory
/// test scheduling, and the support-bundle download. Contract frozen in
/// .deep-build/diagnostics-contract.md. Only /diagnostics/health and
/// /diagnostics/cooling carry AllowPanel() - the only two the panel widget
/// consumes; every other GET stays on the default token auth. The memory test
/// POST/DELETE also stay on the default auth since scheduling a reboot
/// diagnostic is a dashboard-only action; bundle/download is LocalhostOnly
/// like the existing /diagnostics/open-logs route.
/// </summary>
public static class DiagnosticsHealthRoutes
{
    // EventLogMonitor's backfill window is 30 days (DiagnosticEventCatalog),
    // so the store never actually holds more history than that regardless of
    // a larger requested window.
    private const int MaxIncidentDays = 30;

    public static void MapDiagnosticsHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/diagnostics/health", (DiagnosticsHealthModel model) => model.BuildHealth()).AllowPanel();

        app.MapGet("/diagnostics/incidents", (int? days, EventLogMonitor events, SteamGameLibraryCache steamCache) =>
        {
            var windowDays = Math.Clamp(days ?? MaxIncidentDays, 1, MaxIncidentDays);
            return new IncidentsResponse
            {
                Supported = OperatingSystem.IsWindows(),
                WindowDays = windowDays,
                Incidents = DecorateGameCrashes(events.Snapshot(windowDays), steamCache),
            };
        });

        app.MapGet("/diagnostics/smart", (SmartHealthMonitor smart) => smart.Snapshot());

        app.MapGet("/diagnostics/memory", (MemoryDiagnosticOrchestrator memDiag) => BuildMemoryResponse(memDiag));

        app.MapPost("/diagnostics/memory/test", (MemoryDiagnosticOrchestrator memDiag) =>
        {
            memDiag.Schedule();
            return new MemoryTestScheduleResponse { Scheduled = memDiag.IsScheduled(), RequiresReboot = true };
        });

        app.MapDelete("/diagnostics/memory/test", (MemoryDiagnosticOrchestrator memDiag) =>
        {
            memDiag.Cancel();
            return new MemoryTestCancelResponse { Scheduled = memDiag.IsScheduled() };
        });

        app.MapGet("/diagnostics/gpu", (GpuHealthMonitor gpu, EventLogMonitor events) => BuildGpuResponse(gpu, events));

        app.MapGet("/diagnostics/cooling", (CoolingStallDetector cooling) => cooling.Snapshot()).AllowPanel();

        app.MapGet("/diagnostics/system", (PnpProblemScanner pnp, EventLogMonitor events) => BuildSystemResponse(pnp, events));

        app.MapGet("/diagnostics/bundle/download", (
            DiagnosticsHealthModel healthModel,
            EventLogMonitor events,
            SteamGameLibraryCache steamCache,
            SmartHealthMonitor smart,
            MemoryDiagnosticOrchestrator memDiag,
            GpuHealthMonitor gpu,
            CoolingStallDetector cooling,
            PnpProblemScanner pnp) =>
        {
            var health = healthModel.BuildHealth();
            var incidents = new IncidentsResponse
            {
                Supported = OperatingSystem.IsWindows(),
                WindowDays = MaxIncidentDays,
                Incidents = DecorateGameCrashes(events.Snapshot(MaxIncidentDays), steamCache),
            };
            var smartSnapshot = smart.Snapshot();
            var memory = BuildMemoryResponse(memDiag);
            var gpuResponse = BuildGpuResponse(gpu, events);
            var coolingSnapshot = cooling.Snapshot();
            var system = BuildSystemResponse(pnp, events);

            var zipBytes = DiagnosticsBundleBuilder.Build(health, incidents, smartSnapshot, memory, gpuResponse, coolingSnapshot, system);
            var fileName = $"nexus-diagnostics-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            return Results.File(zipBytes, "application/zip", fileName);
        }).LocalhostOnly();
    }

    private static MemoryHealthResponse BuildMemoryResponse(MemoryDiagnosticOrchestrator memDiag)
    {
        var info = MemoryInfoProvider.GetSnapshot();
        return new MemoryHealthResponse
        {
            Supported = info.Supported,
            Modules = info.Modules,
            XmpLikelyActive = info.XmpLikelyActive,
            LastTest = memDiag.LastResult(),
            TestScheduled = memDiag.IsScheduled(),
        };
    }

    private static GpuHealthResponse BuildGpuResponse(GpuHealthMonitor gpu, EventLogMonitor events)
    {
        var counts = events.CountsSince(TimeSpan.FromDays(30));
        var tdr = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr);
        var driverErr = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceGpuDriver);
        var snapshot = gpu.Snapshot();

        var gpus = snapshot.Gpus.Select(g => new GpuInfoWire
        {
            Name = g.Name,
            DriverVersion = g.DriverVersion,
            TemperatureC = g.TemperatureC,
            PowerW = g.PowerW,
            Throttle = g.Throttle,
            RecentTdrCount = tdr,
            RecentDriverErrorCount = driverErr,
        }).ToList();

        return new GpuHealthResponse { Supported = snapshot.Supported, Gpus = gpus };
    }

    private static SystemDiagnosticsResponse BuildSystemResponse(PnpProblemScanner pnp, EventLogMonitor events)
    {
        var counts = events.CountsSince(TimeSpan.FromDays(30));
        var snapshot = pnp.Snapshot();

        return new SystemDiagnosticsResponse
        {
            Supported = snapshot.Supported,
            PnpProblems = snapshot.Devices,
            Counts30d = new SystemDiagnosticsCounts
            {
                Whea = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceWhea),
                Bugchecks = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceBugcheck),
                DirtyShutdowns = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDirtyShutdown),
                DiskErrors = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDisk),
                Tdrs = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr),
                GpuDriverErrors = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceGpuDriver),
                AppCrashes = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceAppCrash),
            },
        };
    }

    private static IReadOnlyList<DiagnosticIncident> DecorateGameCrashes(
        IReadOnlyList<DiagnosticIncident> incidents, SteamGameLibraryCache steamCache)
    {
        var libraries = steamCache.GetLibraryPaths();
        if (libraries.Count == 0)
        {
            return incidents;
        }

        var result = new List<DiagnosticIncident>(incidents.Count);
        foreach (var incident in incidents)
        {
            if (incident.App is { } app && !string.IsNullOrEmpty(app.Path)
                && libraries.Any(lib => IsUnderLibrary(app.Path, lib)))
            {
                result.Add(incident with { App = app with { IsGame = true } });
            }
            else
            {
                result.Add(incident);
            }
        }
        return result;
    }

    // A plain StartsWith would match "D:\SteamLibrary2\..." against library
    // "D:\SteamLibrary" - require the prefix to end exactly at a directory
    // boundary (or the whole path) before declaring the crash under it.
    private static bool IsUnderLibrary(string path, string libraryPath)
    {
        var lib = libraryPath.TrimEnd('\\', '/');
        if (!path.StartsWith(lib, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return path.Length == lib.Length || path[lib.Length] is '\\' or '/';
    }
}

/// <summary>Caches SteamLibraryLocator's library paths for 10 minutes - every
/// appCrash incident on every /diagnostics/incidents poll would otherwise
/// re-read the registry and libraryfolders.vdf.</summary>
public sealed class SteamGameLibraryCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private List<string> _paths = new();
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public IReadOnlyList<string> GetLibraryPaths()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _cachedAtUtc >= CacheTtl)
            {
                _paths = SteamLibraryLocator.EnumerateLibraryPaths().ToList();
                _cachedAtUtc = now;
            }
            return _paths;
        }
    }
}

// ----- Wire response wrappers (module DTOs don't match the contract JSON exactly) -----

public sealed record IncidentsResponse
{
    public bool Supported { get; init; }
    public int WindowDays { get; init; }
    public IReadOnlyList<DiagnosticIncident> Incidents { get; init; } = Array.Empty<DiagnosticIncident>();
}

public sealed record GpuInfoWire
{
    public string Name { get; init; } = "";
    public string? DriverVersion { get; init; }
    public double? TemperatureC { get; init; }
    public double? PowerW { get; init; }
    public GpuThrottleInfo Throttle { get; init; } = new(Array.Empty<string>(), null, null, null, null);
    public int RecentTdrCount { get; init; }
    public int RecentDriverErrorCount { get; init; }
}

public sealed record GpuHealthResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<GpuInfoWire> Gpus { get; init; } = Array.Empty<GpuInfoWire>();
}

public sealed record MemoryHealthResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public bool? XmpLikelyActive { get; init; }
    public MemoryTestResult? LastTest { get; init; }
    public bool TestScheduled { get; init; }
}

public sealed record MemoryTestScheduleResponse
{
    public bool Scheduled { get; init; }
    public bool RequiresReboot { get; init; }
}

public sealed record MemoryTestCancelResponse
{
    public bool Scheduled { get; init; }
}

public sealed record SystemDiagnosticsCounts
{
    public int Whea { get; init; }
    public int Bugchecks { get; init; }
    public int DirtyShutdowns { get; init; }
    public int DiskErrors { get; init; }
    public int Tdrs { get; init; }
    public int GpuDriverErrors { get; init; }
    public int AppCrashes { get; init; }
}

public sealed record SystemDiagnosticsResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<PnpProblemDevice> PnpProblems { get; init; } = Array.Empty<PnpProblemDevice>();
    public SystemDiagnosticsCounts Counts30d { get; init; } = new();
}
