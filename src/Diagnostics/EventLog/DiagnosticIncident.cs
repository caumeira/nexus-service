using System;
using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>Contract severity values for DiagnosticIncident.Severity.</summary>
public static class DiagnosticSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>
/// A single classified Windows Event Log incident. Plain record, no JSON
/// attributes here; the integrator registers this type in AppJsonContext,
/// whose camelCase naming policy maps these PascalCase properties onto the
/// REST contract's id/timeUtc/source/severity/title/detail/app/data fields.
/// </summary>
public sealed record DiagnosticIncident
{
    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    /// <summary>Channel + "/" + EventRecordID.</summary>
    public string Id { get; init; } = "";
    public DateTime TimeUtc { get; init; }
    /// <summary>whea | bugcheck | dirtyShutdown | disk | tdr | gpuDriver | appCrash | liveKernel | memDiag.</summary>
    public string Source { get; init; } = "";
    /// <summary>info | warning | critical; see DiagnosticSeverity.</summary>
    public string Severity { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Non-null only when Source is "appCrash". IsGame is always false here; the integrator fills it via Steam library match.</summary>
    public DiagnosticAppInfo? App { get; init; }
    public IReadOnlyDictionary<string, string> Data { get; init; } = EmptyData;
}

/// <summary>The crashing application, populated only for Source == "appCrash".</summary>
public sealed record DiagnosticAppInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string ExceptionCode { get; init; } = "";
    public string FaultingModule { get; init; } = "";
    public bool IsGame { get; init; }
}
