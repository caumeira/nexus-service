using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class ExternalToolManagerTests : IDisposable
{
    private readonly string _root;     // cache root
    private readonly string _preload;  // app-bundle preload dir

    public ExternalToolManagerTests()
    {
        var b = Path.Combine(Path.GetTempPath(), "nexus-toolmgr-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(b, "cache");
        _preload = Path.Combine(b, "preload");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_preload);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    private ExternalToolSpec Spec(string? preload = null, ToolSession session = ToolSession.System) => new(
        ToolId: "test-tool",
        Variant: "v1",
        ManifestUrl: "https://assets.hellonexus.com/test-tool/v1/latest.json",
        DownloadUrlBase: "https://assets.hellonexus.com/test-tool/v1",
        FilePattern: "*.bin",
        Launch: new ToolLaunchOptions(Hidden: true, Session: session),
        PreloadDir: preload);

    [Fact]
    public async Task ResolveAsync_downloads_hash_pinned_binary_from_manifest()
    {
        var payload = RandomBytes(2048);
        var manifest = ManifestJson("tool.bin", Sha256Hex(payload), payload.Length);
        var http = new HttpClient(new RouteHandler(manifest, payload));
        var mgr = new ExternalToolManager(http, _root);

        var path = await mgr.ResolveAsync(Spec());

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path!));
        Assert.StartsWith(_root, path!);
    }

    [Fact]
    public async Task ResolveAsync_rejects_binary_when_manifest_hash_is_wrong()
    {
        var payload = RandomBytes(512);
        var manifest = ManifestJson("tool.bin", new string('0', 64), payload.Length); // wrong hash
        var http = new HttpClient(new RouteHandler(manifest, payload));
        var mgr = new ExternalToolManager(http, _root);

        // Hash mismatch is swallowed inside resolve → returns null, nothing cached.
        var path = await mgr.ResolveAsync(Spec());

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(_root, "test-tool", "v1", "tool.bin")));
    }

    [Fact]
    public async Task ResolveAsync_uses_bundled_pin_without_touching_network()
    {
        var payload = RandomBytes(1024);
        var pinned = Path.Combine(_preload, "tool-1.0.0.bin");
        await File.WriteAllBytesAsync(pinned, payload);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("tool-1.0.0.bin", Sha256Hex(payload), payload.Length));

        var handler = new ExplodingHandler();
        var mgr = new ExternalToolManager(new HttpClient(handler), _root);

        var path = await mgr.ResolveAsync(Spec(preload: _preload));

        Assert.Equal(pinned, path);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_offline_and_unpinned()
    {
        var http = new HttpClient(new RouteHandler(manifestJson: null, payload: null,
            status: HttpStatusCode.NotFound));
        var mgr = new ExternalToolManager(http, _root);

        Assert.Null(await mgr.ResolveAsync(Spec(preload: _preload)));
    }

    [Fact]
    public void GetStatus_reports_NoDevice_when_absent_and_not_running()
    {
        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        Assert.Equal(ToolStatus.NoDevice, mgr.GetStatus("test-tool", devicePresent: false));
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool", devicePresent: true));
    }

    [Fact]
    public async Task LaunchAsync_runs_then_single_instances_then_terminates()
    {
        // The launch path uses a real long-running child; gate to Unix where we can
        // mint an executable sleeper script. The Windows launch path (same .NET API)
        // is exercised in the PC e2e phase.
        if (OperatingSystem.IsWindows()) return;

        var sleeper = MakeSleeperScript();
        var payload = await File.ReadAllBytesAsync(sleeper);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("sleeper.sh", Sha256Hex(payload), payload.Length));

        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        var spec = Spec(preload: _preload);

        await mgr.LaunchAsync(spec);
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        // Second launch while running is a no-op (single instance).
        await mgr.LaunchAsync(spec);
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        mgr.Terminate("test-tool");
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool"));

        // TerminateAll is idempotent after everything is already down.
        mgr.TerminateAll();
    }

    [Fact]
    public async Task TerminateAll_kills_running_tool_on_shutdown()
    {
        if (OperatingSystem.IsWindows()) return;

        var sleeper = MakeSleeperScript();
        var payload = await File.ReadAllBytesAsync(sleeper);
        await File.WriteAllTextAsync(Path.Combine(_preload, "bundled.json"),
            BundledPinJson("sleeper.sh", Sha256Hex(payload), payload.Length));

        var mgr = new ExternalToolManager(new HttpClient(new ExplodingHandler()), _root);
        await mgr.LaunchAsync(Spec(preload: _preload));
        Assert.Equal(ToolStatus.Running, mgr.GetStatus("test-tool"));

        await mgr.StopAsync(CancellationToken.None); // hosted-service shutdown → TerminateAll
        Assert.Equal(ToolStatus.NotRunning, mgr.GetStatus("test-tool"));
    }

    // ── Helpers ──

    private string MakeSleeperScript()
    {
        var path = Path.Combine(_preload, "sleeper.sh");
        File.WriteAllText(path, "#!/bin/sh\nsleep 30\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private static string ManifestJson(string fileName, string sha256, long size) =>
        "{\"latestVersion\":\"1.0.0\",\"versions\":{\"1.0.0\":{" +
        "\"version\":\"1.0.0\",\"fileName\":\"" + fileName + "\"," +
        "\"url\":\"https://assets.hellonexus.com/test-tool/v1/" + fileName + "\"," +
        "\"sha256\":\"" + sha256 + "\",\"size\":" + size + "}}}";

    private static string BundledPinJson(string fileName, string sha256, long size) =>
        "{\"fileName\":\"" + fileName + "\",\"sha256\":\"" + sha256 + "\",\"size\":" + size + "}";

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        new Random(7).NextBytes(b);
        return b;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly string? _manifestJson;
        private readonly byte[]? _payload;
        private readonly HttpStatusCode _status;

        public RouteHandler(string? manifestJson, byte[]? payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            _manifestJson = manifestJson;
            _payload = payload;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(_status));

            var url = request.RequestUri!.ToString();
            if (url.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(_manifestJson ?? "", Encoding.UTF8, "application/json") });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(_payload ?? Array.Empty<byte>()) });
        }
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("network should not be touched on this path");
        }
    }
}
