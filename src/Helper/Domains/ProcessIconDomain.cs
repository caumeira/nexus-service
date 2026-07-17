#if WINDOWS
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

public sealed class ProcessIconRequest { public string ExePath { get; set; } = ""; }
public sealed class ProcessIconResult { public byte[] Bytes { get; set; } = Array.Empty<byte>(); }

/// <summary>
/// Service-side outbound facade for process-executable icon extraction.
/// Runs through the user-session helper for the same reason shortcuts icons
/// do (ShortcutsDomain) - icon extraction reuses WindowsIconExtractor, which
/// only ever runs inside the helper today.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessIconCommands
{
    public static async Task<byte[]> ExtractAsync(HelperRegistry r, string exePath, CancellationToken ct = default)
        => Read(await InvokeAsync(r, exePath, ct), AppJsonContext.Default.ProcessIconResult)?.Bytes
           ?? Array.Empty<byte>();

    private static async Task<HelperResult?> InvokeAsync(HelperRegistry r, string exePath, CancellationToken ct)
    {
        var conn = r.GetAny();
        if (conn is null) return null;
        return await conn.SendCommandAsync(
            "process-icon.extract", new ProcessIconRequest { ExePath = exePath },
            AppJsonContext.Default.ProcessIconRequest, timeoutMs: 6000, ct: ct).ConfigureAwait(false);
    }

    private static T? Read<T>(HelperResult? r, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (r is null || !r.Ok || r.Payload is null) return default;
        try { return JsonSerializer.Deserialize(r.Payload.Value, typeInfo); }
        catch { return default; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProcessIconHandler
{
    private const int IconSizePx = 256;

    private readonly IWindowsIconExtractor _extractor;

    public ProcessIconHandler(IWindowsIconExtractor extractor) { _extractor = extractor; }

    public void Register(HelperHandlerRegistry registry) =>
        registry.Register("process-icon.extract", (env, _) => Reply(env, Extract(ReadReq(env).ExePath)));

    // No disk-cache reuse: unlike shortcuts (Get-StartApps enumeration + a
    // recursive .lnk search per miss), resolving a running process to its
    // exe path is already an in-memory lookup and extraction is one GDI
    // call - re-extracting on a cold cache costs little, so the service-side
    // in-memory LRU (ProcessIconCache) is the only cache this feature needs.
    private ProcessIconResult Extract(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return new ProcessIconResult();
        }

        byte[] png;
        try { png = _extractor.ExtractPng(exePath, IconSizePx); }
        catch { png = Array.Empty<byte>(); }
        return new ProcessIconResult { Bytes = png };
    }

    private static ProcessIconRequest ReadReq(HelperEnvelope env)
        => env.Payload is null
            ? new ProcessIconRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProcessIconRequest) ?? new ProcessIconRequest();

    private static Task<HelperResult> Reply(HelperEnvelope env, ProcessIconResult value)
        => Task.FromResult(new HelperResult
        {
            Id = env.Id ?? "",
            Ok = true,
            Payload = JsonSerializer.SerializeToElement(value, AppJsonContext.Default.ProcessIconResult),
        });
}
#endif
