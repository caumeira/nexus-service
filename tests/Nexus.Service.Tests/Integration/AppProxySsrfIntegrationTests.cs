using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// One host whose AppRegistry is backed by a temp fixture bundle whose
/// manifest <c>net.fetch</c> allowlist deliberately permits private / reserved
/// hosts - the "malicious signed manifest" SSRF threat. Drives the real
/// <c>POST /apps-api/proxy</c>; the proxy must refuse, proving the
/// IsPrivateOrReservedAddress guard overrides the manifest allowlist.
/// </summary>
public sealed class SsrfAppFactory : NexusAppFactory
{
    public const string AppId = "com.test.ssrf";

    private readonly string _fixtureRoot;

    public SsrfAppFactory()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "nexus-ssrf-fixture-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(_fixtureRoot, AppId);
        Directory.CreateDirectory(bundle);

        // Derive the allowlist from each probe URL's Uri.Host so the allowlist
        // match never fails for the wrong reason - every request reaches the
        // SSRF guard.
        var hosts = new[]
        {
            "https://127.0.0.1/x", "https://169.254.169.254/", "https://10.0.0.5/",
            "https://192.168.0.1/", "https://100.64.0.1/", "https://[::1]/",
        }.Select(u => new Uri(u).Host).Distinct();
        var allow = string.Join(", ", hosts.Select(h => "\"" + h + "\""));

        File.WriteAllText(Path.Combine(bundle, "manifest.json"), $$"""
        {
          "schema": "nexus.app/1",
          "id": "{{AppId}}",
          "name": "SSRF Fixture",
          "version": "1.0.0",
          "runtime": "sdk",
          "surfaces": ["dashboard"],
          "sizes": ["2x2"],
          "default_size": "2x2",
          "capabilities": { "net.fetch": [{{allow}}] }
        }
        """);
        File.WriteAllText(Path.Combine(bundle, "widget.mjs"), "export const mount = () => {};");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppRegistry>();
            services.AddSingleton(new AppRegistry(() => new List<AppInstallPaths.Root>
            {
                new(_fixtureRoot, AppInstallPaths.Source.User),
            }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_fixtureRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}

public sealed class AppProxySsrfIntegrationTests : IClassFixture<SsrfAppFactory>
{
    private readonly SsrfAppFactory _factory;

    public AppProxySsrfIntegrationTests(SsrfAppFactory factory) => _factory = factory;

    private async Task<string> ProxyError(string appId, string url, string method = "GET")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        var body = $"{{\"appId\":\"{appId}\",\"url\":\"{url}\",\"method\":\"{method}\"}}";
        var res = await client.PostAsync("/apps-api/proxy",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return await res.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://192.168.0.1/")]
    [InlineData("https://100.64.0.1/")]
    [InlineData("https://[::1]/")]
    public async Task Proxy_refuses_reserved_host_even_when_manifest_allows_it(string url)
    {
        var json = await ProxyError(SsrfAppFactory.AppId, url);

        Assert.Contains("non-routable", json);
    }

    [Fact]
    public async Task Proxy_rejects_cleartext_http_scheme()
    {
        var json = await ProxyError(SsrfAppFactory.AppId, "http://127.0.0.1/");

        Assert.Contains("https", json); // "url must be absolute https://"
    }

    [Fact]
    public async Task Proxy_rejects_host_outside_manifest_allowlist()
    {
        // Public host that is NOT allowlisted - rejected before the SSRF check.
        var json = await ProxyError(SsrfAppFactory.AppId, "https://example.org/");

        Assert.Contains("allowlist", json);
    }

    [Fact]
    public async Task Proxy_rejects_unknown_widget_id()
    {
        var json = await ProxyError("com.test.not-installed", "https://127.0.0.1/");

        Assert.Contains("not installed", json);
    }
}
