using System;
using System.Threading;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// Module-worker code sessions: short-lived URL-path tokens that let a
/// Tier 2 widget's ESM worker fetch sibling .js files without needing a
/// Bearer header on every import.
/// </summary>
public class AppCodeSessionServiceTests
{
    [Fact]
    public void Create_returns_token_that_resolves_to_widget_id()
    {
        var svc = new AppCodeSessionService();
        var token = svc.Create("com.hellonexus.test");
        Assert.NotNull(token);
        Assert.NotEmpty(token);
        Assert.Equal("com.hellonexus.test", svc.Resolve(token));
    }

    [Fact]
    public void Tokens_are_unique_across_calls()
    {
        var svc = new AppCodeSessionService();
        var a = svc.Create("com.hellonexus.test");
        var b = svc.Create("com.hellonexus.test");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_token()
    {
        var svc = new AppCodeSessionService();
        Assert.Null(svc.Resolve("not-a-real-token"));
    }

    [Fact]
    public void Expired_session_resolves_to_null_and_is_purged()
    {
        var svc = new AppCodeSessionService();
        var token = svc.Create("com.hellonexus.test", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        Assert.Null(svc.Resolve(token));
        // A second Resolve also returns null (the entry should already be gone
        // from the dictionary on the first miss).
        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Revoke_invalidates_a_live_token()
    {
        var svc = new AppCodeSessionService();
        var token = svc.Create("com.hellonexus.test");
        Assert.NotNull(svc.Resolve(token));
        svc.Revoke(token);
        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Empty_or_null_inputs_are_rejected()
    {
        var svc = new AppCodeSessionService();
        Assert.Null(svc.Resolve(""));
        Assert.Null(svc.Resolve("   "));
    }
}
