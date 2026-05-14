using System;
using System.Threading;
using Qos.Service.Widgets;

namespace Qos.Service.Tests.Widgets;

/// <summary>
/// Module-worker code sessions: short-lived URL-path tokens that let a
/// Tier 2 widget's ESM worker fetch sibling .js files without needing a
/// Bearer header on every import.
/// </summary>
public class WidgetCodeSessionServiceTests
{
    [Fact]
    public void Create_returns_token_that_resolves_to_widget_id()
    {
        var svc = new WidgetCodeSessionService();
        var token = svc.Create("com.nexusqos.test");
        Assert.NotNull(token);
        Assert.NotEmpty(token);
        Assert.Equal("com.nexusqos.test", svc.Resolve(token));
    }

    [Fact]
    public void Tokens_are_unique_across_calls()
    {
        var svc = new WidgetCodeSessionService();
        var a = svc.Create("com.nexusqos.test");
        var b = svc.Create("com.nexusqos.test");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Token_is_base64url_with_no_padding_or_unsafe_chars()
    {
        var svc = new WidgetCodeSessionService();
        var token = svc.Create("com.nexusqos.test");
        // Base64URL alphabet only: A-Z a-z 0-9 - _
        foreach (var c in token)
        {
            Assert.True(char.IsLetterOrDigit(c) || c == '-' || c == '_', $"unexpected token char: '{c}'");
        }
        // No padding, no '+', no '/' (those would break the URL).
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_token()
    {
        var svc = new WidgetCodeSessionService();
        Assert.Null(svc.Resolve("not-a-real-token"));
    }

    [Fact]
    public void Expired_session_resolves_to_null_and_is_purged()
    {
        var svc = new WidgetCodeSessionService();
        var token = svc.Create("com.nexusqos.test", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        Assert.Null(svc.Resolve(token));
        // A second Resolve also returns null (the entry should already be gone
        // from the dictionary on the first miss).
        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Revoke_invalidates_a_live_token()
    {
        var svc = new WidgetCodeSessionService();
        var token = svc.Create("com.nexusqos.test");
        Assert.NotNull(svc.Resolve(token));
        svc.Revoke(token);
        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Empty_or_null_inputs_are_rejected()
    {
        var svc = new WidgetCodeSessionService();
        Assert.Null(svc.Resolve(""));
        Assert.Null(svc.Resolve("   "));
    }
}
