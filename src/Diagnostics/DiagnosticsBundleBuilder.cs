using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Builds the GET /diagnostics/bundle/download support ZIP fully in memory -
/// no temp files, nothing to clean up on a failed request. Every JSON entry
/// reuses the same wire types + AppJsonContext source-gen metadata the live
/// GET routes return, so the bundle always matches what the diagnostics page
/// itself displays.
/// </summary>
public static class DiagnosticsBundleBuilder
{
    private const int LogTailLines = 500;
    private const string StartupSnapshotStart = "===== Nexus startup snapshot =====";
    private const string StartupSnapshotEnd = "===== end snapshot =====";

    public static byte[] Build(
        DiagnosticsHealthResponse health,
        IncidentsResponse incidents,
        SmartSnapshot smart,
        MemoryHealthResponse memory,
        GpuHealthResponse gpu,
        CoolingStallSnapshot cooling,
        SystemDiagnosticsResponse system,
        byte[]? reportPdf = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "health.json", health, AppJsonContext.Default.DiagnosticsHealthResponse);
            WriteJson(zip, "incidents.json", incidents, AppJsonContext.Default.IncidentsResponse);
            WriteJson(zip, "smart.json", smart, AppJsonContext.Default.SmartSnapshot);
            WriteJson(zip, "memory.json", memory, AppJsonContext.Default.MemoryHealthResponse);
            WriteJson(zip, "gpu.json", gpu, AppJsonContext.Default.GpuHealthResponse);
            WriteJson(zip, "cooling.json", cooling, AppJsonContext.Default.CoolingStallSnapshot);
            WriteJson(zip, "system.json", system, AppJsonContext.Default.SystemDiagnosticsResponse);
            WriteText(zip, "system-profile.txt", ReadStartupSnapshot());
            WriteText(zip, "service-log-tail.txt", ReadLogTail());
            if (reportPdf is { Length: > 0 })
            {
                WriteBytes(zip, "report.pdf", reportPdf);
            }
        }
        return ms.ToArray();
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, typeInfo);
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void WriteBytes(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static string ReadLogTail()
    {
        var path = CurrentLogPath();
        if (path is null || !File.Exists(path))
        {
            return "";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }
            return string.Join(Environment.NewLine, lines.TakeLast(LogTailLines));
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string ReadStartupSnapshot()
    {
        var path = CurrentLogPath();
        if (path is null || !File.Exists(path))
        {
            return "";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            var capturing = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!capturing && line.Contains(StartupSnapshotStart, StringComparison.Ordinal))
                {
                    capturing = true;
                }
                if (capturing)
                {
                    lines.Add(line);
                }
                if (capturing && line.Contains(StartupSnapshotEnd, StringComparison.Ordinal))
                {
                    break;
                }
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string? CurrentLogPath() =>
        ServiceLog.LogFilePath ?? Path.Combine(ServiceLog.LogsDirectory, "nexus-service.log");
}
