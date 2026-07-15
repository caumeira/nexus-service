using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Platform;

namespace Nexus.Service.Common.ExternalTools;

[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ToolBundledPin))]
internal partial class ExternalToolsJsonContext : JsonSerializerContext;

/// <summary>
/// Generic "device shows up -> fetch its sidecar payload -> install/run it" manager
/// (Linear NEX-13). One singleton resolves a tool's artifact on disk - fetching it
/// from <c>assets.hellonexus.com</c> on first use, hash-pinned, and caching it -
/// then routes install/launch to the <see cref="IToolInstallStrategy"/> for the
/// spec's <see cref="ExternalToolSpec.Target"/>. Generic over the app's driver
/// manifest block; no consuming app is named here.
///
/// Trust: a tool runs only when a bundled app declares it (the <c>driver</c>
/// manifest block, gated by install source in the registry); this class is the
/// actuator, not the gate.
/// </summary>
public sealed class ExternalToolManager : IHostedService, IToolResolver
{
    /// <summary>Dev-only escape hatch: set to "1" to allow an unverified glob match
    /// when no manifest and no hash-pinned <c>bundled.json</c> are available. Off in
    /// shipping builds - every resolved binary is hash-pinned.</summary>
    private const string AllowUnverifiedEnv = "NEXUS_TOOLS_ALLOW_UNVERIFIED";

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly IReadOnlyDictionary<ToolTarget, IToolInstallStrategy> _strategies;
    private readonly ConcurrentDictionary<string, Task<string?>> _resolving = new(StringComparer.Ordinal);

    public ExternalToolManager(IEnumerable<IToolInstallStrategy> strategies)
        : this(strategies, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, ResolveDefaultRoot()) { }

    /// <summary>Test seam: inject an <see cref="HttpClient"/> and a temp cache root;
    /// defaults to the host-exe strategy.</summary>
    public ExternalToolManager(HttpClient http, string root)
        : this(new IToolInstallStrategy[] { new HostExeInstallStrategy() }, http, root) { }

    public ExternalToolManager(IEnumerable<IToolInstallStrategy> strategies, HttpClient http, string root)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _strategies = (strategies ?? throw new ArgumentNullException(nameof(strategies)))
            .ToDictionary(s => s.Target);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TerminateAll();
        return Task.CompletedTask;
    }

    // ── Resolve ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the path to the tool's verified binary, fetching + caching it if
    /// necessary, or null if it can't be resolved. Concurrent calls for the same
    /// <c>(ToolId, Variant)</c> share one in-flight resolve.
    /// </summary>
    public async Task<string?> ResolveAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        var key = spec.ToolId + " " + spec.Variant;
        var task = _resolving.GetOrAdd(key, _ => ResolveCoreAsync(spec, ct));
        try { return await task; }
        finally { _resolving.TryRemove(key, out _); }
    }

    private async Task<string?> ResolveCoreAsync(ExternalToolSpec spec, CancellationToken ct)
    {
        var cacheDir = ToolDir(spec);

        // 1. bundled.json pin (cache dir first, then the app's preload dir). Verified
        //    by hash so the offline/air-gapped path stays trustworthy.
        foreach (var baseDir in EnumerateBaseDirs(cacheDir, spec.PreloadDir))
        {
            var pin = TryReadPin(baseDir);
            if (pin is null) continue;
            var pinned = Path.Combine(baseDir, pin.FileName);
            if (await VerifiedDownload.IsValidAsync(pinned, pin.Sha256, pin.Size, ct))
                return pinned;
            ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} bundled.json pin failed verification: {pin.FileName}");
        }

        // 2. manifest fetch → hash-pinned download into the cache dir.
        try
        {
            var manifest = await FetchManifestAsync(spec.ManifestUrl, ct);
            if (manifest is not null
                && manifest.Versions.TryGetValue(manifest.LatestVersion, out var v)
                && !string.IsNullOrWhiteSpace(v.FileName))
            {
                var url = string.IsNullOrWhiteSpace(v.Url)
                    ? $"{spec.DownloadUrlBase.TrimEnd('/')}/{v.FileName}"
                    : v.Url!;
                var dest = Path.Combine(cacheDir, v.FileName);
                await VerifiedDownload.DownloadAsync(_http, url, dest, v.Sha256, v.Size, ct);
                return dest;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} manifest/download failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. dev-only unverified glob fallback.
        if (AllowUnverified())
        {
            foreach (var baseDir in EnumerateBaseDirs(cacheDir, spec.PreloadDir))
            {
                var match = SafeGlobFirst(baseDir, spec.FilePattern);
                if (match is not null)
                {
                    ServiceLog.Warn($"[tools] {spec.ToolId}/{spec.Variant} using UNVERIFIED glob match {Path.GetFileName(match)} ({AllowUnverifiedEnv}=1)");
                    return match;
                }
            }
        }

        return null;
    }

    private async Task<ToolManifest?> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
        using var resp = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, ExternalToolsJsonContext.Default.ToolManifest, ct);
    }

    /// <summary>Fetch the remote manifest and return its latest version entry
    /// (includes <see cref="ToolVersion.VersionCode"/>), without downloading the
    /// payload. Returns null when the manifest is unavailable.</summary>
    public async Task<ToolVersion?> GetLatestAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        try
        {
            var manifest = await FetchManifestAsync(spec.ManifestUrl, ct);
            if (manifest is null) return null;
            return manifest.Versions.TryGetValue(manifest.LatestVersion, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetch the remote manifest and return the latest version string,
    /// or null when unavailable.</summary>
    public async Task<string?> GetLatestVersionAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        var v = await GetLatestAsync(spec, ct);
        return v?.Version;
    }

    // ── Launch / lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolve and run the tool on its target medium, routed by
    /// <see cref="ExternalToolSpec.Target"/>. Single-instance and process tracking
    /// are the strategy's responsibility. A spec whose target has no registered
    /// strategy is a no-op.
    /// </summary>
    public Task LaunchAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(spec.Target, out var strategy))
        {
            ServiceLog.Warn($"[tools] {spec.ToolId}: no install strategy for target {spec.Target}; not launched");
            return Task.CompletedTask;
        }
        return strategy.LaunchAsync(spec, this, ct);
    }

    /// <summary>
    /// Tear down a single tool on whichever strategy owns it. True only when a
    /// strategy confirms the process is gone - a caller that then touches the
    /// device relies on this to know it is the only writer.
    /// </summary>
    public bool Terminate(string toolId)
    {
        var gone = false;
        foreach (var strategy in _strategies.Values)
            gone |= strategy.Terminate(toolId);
        return gone;
    }

    /// <summary>Tear down every tracked tool. Called from <see cref="StopAsync"/>,
    /// and directly from the Windows fast-shutdown path, which runs no hosted
    /// StopAsync - without that call the tools outlive the service.</summary>
    public void TerminateAll()
    {
        foreach (var strategy in _strategies.Values)
            strategy.TerminateAll();
    }

    /// <summary>
    /// Current status for a tool. <paramref name="devicePresent"/> distinguishes
    /// "no hardware" (NoDevice) from "hardware present but not running" (NotRunning);
    /// pass null when presence is unknown.
    /// </summary>
    public ToolStatus GetStatus(string toolId, bool? devicePresent = null)
    {
        foreach (var strategy in _strategies.Values)
        {
            var status = strategy.GetStatus(toolId);
            if (status is ToolStatus.Running or ToolStatus.Failed) return status;
        }
        if (devicePresent == false) return ToolStatus.NoDevice;
        return ToolStatus.NotRunning;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private string ToolDir(ExternalToolSpec spec)
    {
        var dir = Path.Combine(_root, Sanitize(spec.ToolId), Sanitize(spec.Variant));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateBaseDirs(string cacheDir, string? preloadDir)
    {
        yield return cacheDir;
        if (!string.IsNullOrEmpty(preloadDir) && Directory.Exists(preloadDir))
            yield return preloadDir;
    }

    private static ToolBundledPin? TryReadPin(string baseDir)
    {
        var path = Path.Combine(baseDir, "bundled.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize(fs, ExternalToolsJsonContext.Default.ToolBundledPin);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGlobFirst(string baseDir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal)) return null;
        if (!Directory.Exists(baseDir)) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(baseDir, pattern, SearchOption.TopDirectoryOnly))
                return f;
        }
        catch { /* best effort */ }
        return null;
    }

    private static bool AllowUnverified()
        => string.Equals(Environment.GetEnvironmentVariable(AllowUnverifiedEnv), "1", StringComparison.Ordinal);

    private static string Sanitize(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Tool path segment is required.", nameof(segment));
        foreach (var c in segment)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new ArgumentException($"Tool path segment '{segment}' contains illegal characters.", nameof(segment));
        }
        return segment;
    }

    private static string ResolveDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope so the LocalSystem service owns the cache, like the
            // firmware store.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "drivers");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(xdg, "Nexus", "drivers");
    }
}
