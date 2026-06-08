using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class AppProxyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppRegistry _registry;

    public AppProxyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-proxy-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        WriteWidget("com.hellonexus.allowed", new[] { "api.weather.gov", "*.example.com" });
        WriteWidget("com.hellonexus.empty",   Array.Empty<string>());
        _registry = new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteWidget(string id, string[] netFetch)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var manifest = new
        {
            schema = "nexus.app/1",
            id,
            name = id,
            version = "1.0.0",
            min_nexus_version = "0.42.0",
            surfaces = new[] { "dashboard" },
            sizes = new[] { "2x2" },
            capabilities = new Dictionary<string, object>
            {
                ["sensors.read"] = Array.Empty<string>(),
                ["rgb.read"] = false,
                ["rgb.write"] = false,
                ["net.fetch"] = netFetch,
                ["config"] = true,
            },
            runtime = "sdk",
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export const mount = () => {};");
    }

    private AppProxyService MakeService(HttpMessageHandler handler)
    {
        var factory = new StubHttpFactory(new HttpClient(handler));
        return new AppProxyService(factory, _registry);
    }

    [Fact]
    public async Task Rejects_unknown_widget()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.unknown", Url = "https://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("not installed", resp.Error);
    }

    [Fact]
    public async Task Rejects_non_https_url()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "http://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("https", resp.Error);
    }

    [Fact]
    public async Task Rejects_host_outside_manifest_allowlist()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://evil.test/data" });
        Assert.False(resp.Ok);
        Assert.Contains("allowlist", resp.Error);
    }

    [Fact]
    public async Task Accepts_wildcard_subdomain_match()
    {
        var svc = MakeService(new StubHandler("application/json", "{\"x\":1}"));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://www.example.com/data" });
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Body);
        Assert.Equal(1, resp.Body!.Value.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Caps_response_body_at_1_MiB()
    {
        var big = new string('a', 1024 * 1024 + 200);
        var svc = MakeService(new StubHandler("text/plain", big));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/big" });
        Assert.NotNull(resp.Error);
        Assert.Contains("truncated", resp.Error);
        Assert.NotNull(resp.BodyText);
        Assert.True(resp.BodyText!.Length <= AppProxyService.MaxBodyBytes);
    }

    [Fact]
    public async Task Json_body_is_decoded_into_Body()
    {
        var svc = MakeService(new StubHandler("application/json", "{\"a\": [1, 2], \"b\": \"hi\"}"));
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.allowed", Url = "https://api.weather.gov/json" });
        Assert.True(resp.Ok);
        Assert.NotNull(resp.Body);
        var b = resp.Body!.Value.GetProperty("b").GetString();
        Assert.Equal("hi", b);
    }

    [Fact]
    public async Task Drops_forbidden_request_headers()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer leaked",
            ["Cookie"] = "session=evil",
            ["X-Custom"] = "ok",
        };
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
            Headers = headers,
        });
        Assert.NotNull(capture.LastRequest);
        Assert.False(capture.LastRequest!.Headers.Contains("Authorization"));
        Assert.False(capture.LastRequest.Headers.Contains("Cookie"));
        Assert.True(capture.LastRequest.Headers.Contains("X-Custom"));
    }

    [Fact]
    public async Task Refuses_when_manifest_allowlist_is_empty()
    {
        var svc = MakeService(new ThrowingHandler());
        var resp = await svc.ExecuteAsync(new AppProxyRequest { AppId = "com.hellonexus.empty", Url = "https://api.weather.gov" });
        Assert.False(resp.Ok);
        Assert.Contains("allowlist", resp.Error);
    }

    [Fact]
    public async Task Sets_default_user_agent_when_widget_does_not_supply_one()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
        });
        Assert.NotNull(capture.LastRequest);
        Assert.True(capture.LastRequest!.Headers.Contains("User-Agent"));
        var ua = string.Join(',', capture.LastRequest.Headers.GetValues("User-Agent"));
        Assert.Contains("Nexus-Widget-Proxy", ua);
    }

    [Fact]
    public async Task Widget_supplied_user_agent_overrides_the_default()
    {
        var capture = new CapturingHandler();
        var svc = MakeService(capture);
        await svc.ExecuteAsync(new AppProxyRequest
        {
            AppId = "com.hellonexus.allowed",
            Url = "https://api.weather.gov/x",
            Headers = new Dictionary<string, string> { ["User-Agent"] = "MyCoolWidget/2.0" },
        });
        var ua = string.Join(',', capture.LastRequest!.Headers.GetValues("User-Agent"));
        Assert.Equal("MyCoolWidget/2.0", ua);
        Assert.DoesNotContain("Nexus-Widget-Proxy", ua);
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpFactory(HttpClient client) { _client = client; }
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _contentType;
        private readonly string _body;
        public StubHandler(string contentType, string body) { _contentType = contentType; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
            return Task.FromResult(msg);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(msg);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("network should not be invoked");
        }
    }
}
