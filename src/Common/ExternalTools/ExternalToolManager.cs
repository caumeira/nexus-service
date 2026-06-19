using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Generic "device shows up → fetch its sidecar executable → run it" manager
/// (Linear NEX-13). One singleton resolves a tool's binary on disk - fetching it
/// from <c>assets.hellonexus.com</c> on first use, hash-pinned, and caching it -
/// then launches and tracks the process (single-instance per <c>ToolId</c>,
/// terminated on service shutdown). Generic over the app's driver manifest block;
/// no consuming app is named here.
///
/// Trust: a tool runs only when a bundled app declares it (the <c>driver</c>
/// manifest block, gated by install source in the registry); this class is the
/// actuator, not the gate.
/// </summary>
public sealed class ExternalToolManager : IHostedService
{
    /// <summary>Dev-only escape hatch: set to "1" to allow an unverified glob match
    /// when no manifest and no hash-pinned <c>bundled.json</c> are available. Off in
    /// shipping builds - every resolved binary is hash-pinned.</summary>
    private const string AllowUnverifiedEnv = "NEXUS_TOOLS_ALLOW_UNVERIFIED";

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<string?>> _resolving = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public ExternalToolManager() : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, ResolveDefaultRoot()) { }

    /// <summary>Test seam: inject an <see cref="HttpClient"/> and a temp cache root.</summary>
    public ExternalToolManager(HttpClient http, string root)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _root = root ?? throw new ArgumentNullException(nameof(root));
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

    // ── Launch / lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolve and launch the tool, single-instance per <c>ToolId</c>. A second call
    /// while the process is alive is a no-op. The auto-launch worker and the manual
    /// dispatch both funnel through here so a hot-plug + a user click can't
    /// double-spawn.
    /// </summary>
    public async Task LaunchAsync(ExternalToolSpec spec, CancellationToken ct = default)
    {
        await _launchGate.WaitAsync(ct);
        try
        {
            if (IsRunning(spec.ToolId)) return;

            var path = await ResolveAsync(spec, ct);
            if (path is null)
            {
                _failed[spec.ToolId] = 1;
                ServiceLog.Warn($"[tools] {spec.ToolId}: no binary resolved; not launched");
                return;
            }

            // Adopt an instance left by a prior service run - a crash / hard-kill
            // skips StopAsync, so the previous driver process can still be alive.
            // Adopting (instead of spawning a duplicate) keeps exactly one driver
            // across restarts, and the next graceful stop still terminates it.
            var existing = FindRunningByImage(path);
            if (existing is not null)
            {
                _failed.TryRemove(spec.ToolId, out _);
                _running[spec.ToolId] = existing;
                ServiceLog.Info($"[tools] {spec.ToolId}: adopted already-running {Path.GetFileName(path)} pid={existing.Id}");
                return;
            }

            var proc = StartProcess(spec, path);
            if (proc is null)
            {
                _failed[spec.ToolId] = 1;
            }
            else
            {
                _failed.TryRemove(spec.ToolId, out _);
                _running[spec.ToolId] = proc;
            }
        }
        finally
        {
            _launchGate.Release();
        }
    }

    /// <summary>Kill a single tool's process (tree) if running.</summary>
    public void Terminate(string toolId)
    {
        if (!_running.TryRemove(toolId, out var proc)) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            ServiceLog.Info($"[tools] terminated {toolId}");
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tools] terminate {toolId}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Kill every tracked tool process. Called from <see cref="StopAsync"/>.</summary>
    public void TerminateAll()
    {
        foreach (var id in _running.Keys.ToArray())
            Terminate(id);
    }

    /// <summary>
    /// Current status for a tool. <paramref name="devicePresent"/> distinguishes
    /// "no hardware" (NoDevice) from "hardware present but not running" (NotRunning);
    /// pass null when presence is unknown.
    /// </summary>
    public ToolStatus GetStatus(string toolId, bool? devicePresent = null)
    {
        if (IsRunning(toolId)) return ToolStatus.Running;
        if (_failed.ContainsKey(toolId)) return ToolStatus.Failed;
        if (devicePresent == false) return ToolStatus.NoDevice;
        return ToolStatus.NotRunning;
    }

    /// <summary>
    /// Find a process already running the resolved binary (by image name), to adopt
    /// across a service restart rather than double-spawn. Returns the first match;
    /// extra handles are disposed (the processes are left alone).
    /// </summary>
    private static Process? FindRunningByImage(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var matches = Process.GetProcessesByName(name);
            if (matches.Length == 0) return null;
            for (var i = 1; i < matches.Length; i++) matches[i].Dispose();
            return matches[0];
        }
        catch
        {
            return null;
        }
    }

    private bool IsRunning(string toolId)
    {
        if (_running.TryGetValue(toolId, out var proc))
        {
            try { if (!proc.HasExited) return true; }
            catch { /* fall through to cleanup */ }
            if (_running.TryRemove(toolId, out var dead))
            {
                try { dead.Dispose(); } catch { /* ignore */ }
            }
        }
        return false;
    }

    private Process? StartProcess(ExternalToolSpec spec, string path)
    {
        try
        {
            if (spec.Launch.Session == ToolSession.User)
                return StartInUserSession(spec, path);

            // System session: launch directly in the service's own (LocalSystem,
            // Session 0) context so the tool runs pre-login. No "runas" verb -
            // LocalSystem is already maximally privileged.
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = spec.Launch.Hidden,
                WindowStyle = spec.Launch.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            var proc = Process.Start(psi);
            ServiceLog.Info($"[tools] launched {spec.ToolId} ({Path.GetFileName(path)}) pid={proc?.Id}");
            return proc;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[tools] launch {spec.ToolId} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private Process? StartInUserSession(ExternalToolSpec spec, string path)
    {
#if WINDOWS
        // Detached cross-session launch via the scheduled-task helper. No handle is
        // retained (schtasks detaches), so a user-session tool's status falls back to
        // NotRunning - image-name tracking is a follow-up. The default System session
        // does not use this path.
        Nexus.Service.Lifecycle.UserHelperBootstrapper.RunInUserSession(
            $"\"{path}\"", $"tools-{spec.ToolId}", "NexusTool");
        ServiceLog.Info($"[tools] launched {spec.ToolId} in user session (schtasks)");
        return null;
#else
        // Non-Windows has no Session-0/desktop split - launch directly.
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = spec.Launch.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        return Process.Start(psi);
#endif
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
            return Path.Combine(programData, "Nexus", "tools");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(xdg, "Nexus", "tools");
    }
}
