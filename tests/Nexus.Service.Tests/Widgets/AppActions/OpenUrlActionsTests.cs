using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

/// <summary>
/// Covers the openUrl validator as a pure function, plus the registered
/// handler's refusal path. The success path is deliberately not driven here:
/// it hands the URL to the machine's real browser.
/// </summary>
public class OpenUrlActionsTests : IDisposable
{
    private static readonly string[] Allowlist = { "api.hellonexus.com", "hyte.com", "*.hyte.com" };

    private readonly string _root;

    public OpenUrlActionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-openurl-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("https://hyte.com/account", true)]
    [InlineData("https://api.hellonexus.com/hyte/me", true)]
    [InlineData("https://support.hyte.com/hc", true)]
    [InlineData("https://a.b.hyte.com/deep", true)]
    [InlineData("https://claims.route.com/", false)]
    [InlineData("https://nothyte.com/", false)]
    [InlineData("http://hyte.com/account", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://user:pw@hyte.com/", false)]
    [InlineData("https://hyte.com/a b", false)]
    [InlineData("https://hyte.com/\"x\"", false)]
    [InlineData("https://hyte.com/caf\u00e9", false)]
    [InlineData("https://hyte.com/support?ref=nexus&v=1", true)]
    [InlineData("mailto:support@hyte.com", true)]
    [InlineData("mailto:support@hyte.com?subject=x", true)]
    [InlineData("mailto:support@hyte.com?subject=x&body=hello%20there", true)]
    [InlineData("mailto:support@hyte.com?cc=someone@hyte.com", false)]
    [InlineData("mailto:support@hyte.com?subject=x&cc=someone@hyte.com", false)]
    [InlineData("mailto:not-an-address", false)]
    [InlineData("mailto:support@hyte.com#evil", false)]
    [InlineData("mailto:one@hyte.com,two@hyte.com", false)]
    [InlineData("mailto:", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Validator_admits_only_allowlisted_https_and_a_plain_mailto(string url, bool expected)
    {
        Assert.Equal(expected, OpenUrlActions.TryValidate(url, Allowlist, out _, out _));
    }

    [Fact]
    public void Validator_rejects_an_overlong_url()
    {
        var url = "https://hyte.com/" + new string('a', OpenUrlActions.MaxUrlLength);

        Assert.False(OpenUrlActions.TryValidate(url, Allowlist, out _, out var reason));
        Assert.Contains("too long", reason);
    }

    [Fact]
    public void Validator_rejects_every_https_host_when_the_app_allowlists_none()
    {
        Assert.False(OpenUrlActions.TryValidate("https://hyte.com/", Array.Empty<string>(), out _, out _));
        Assert.False(OpenUrlActions.TryValidate("https://hyte.com/", null, out _, out _));
    }

    [Fact]
    public void Mailto_is_not_gated_on_the_fetch_allowlist()
    {
        Assert.True(OpenUrlActions.TryValidate("mailto:support@hyte.com", Array.Empty<string>(), out var target, out _));
        Assert.Equal("mailto:support@hyte.com", target);
    }

    [Fact]
    public void Accepted_https_target_is_the_normalised_absolute_uri()
    {
        Assert.True(OpenUrlActions.TryValidate("  https://hyte.com/account  ", Allowlist, out var target, out _));
        Assert.Equal("https://hyte.com/account", target);
    }

    [Fact]
    public async Task Refused_url_acks_false_without_reaching_the_shell()
    {
        // The provider carries no SystemActions, so any attempt to open would
        // throw rather than silently pass.
        var services = new ServiceCollection();
        services.AddSingleton(BuildRegistry("com.hyte.account", new[] { "hyte.com" }));
        var sp = services.BuildServiceProvider();

        var registry = new AppActionRegistry();
        OpenUrlActions.RegisterAll(registry);
        Assert.True(registry.TryGet("system.openUrl", out var handler));

        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"__appId":"com.hyte.account","url":"https://evil.test/steal"}""")!;
        var result = await handler(sp, args, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("url not permitted", result.Value.GetProperty("message").GetString());
    }

    [Fact]
    public void Action_is_listed_for_registration()
    {
        Assert.Contains("system.openUrl", OpenUrlActions.AllActions);
    }

    private AppRegistry BuildRegistry(string appId, string[] netFetch)
    {
        var dir = Path.Combine(_root, appId);
        Directory.CreateDirectory(dir);
        var manifest = new
        {
            schema = "nexus.app/1",
            id = appId,
            name = appId,
            version = "1.0.0",
            min_nexus_version = "0.42.0",
            surfaces = new[] { "dashboard" },
            sizes = new[] { "2x2" },
            capabilities = new Dictionary<string, object>
            {
                ["net.fetch"] = netFetch,
                ["dispatch"] = new[] { "system.openUrl" },
            },
            runtime = "sdk",
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export const mount = () => {};");
        return new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, AppInstallPaths.Source.Bundled),
        });
    }
}
