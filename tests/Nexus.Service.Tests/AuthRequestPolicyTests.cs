using Nexus.Service.Auth;
using Nexus.Service.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nexus.Service.Tests;

public class AuthRequestPolicyTests
{
    [Fact]
    public void ExtractBearerOrQueryToken_AcceptsBearerCaseInsensitive()
    {
        var ctx = NewContext("GET", "/system/cpu/model");
        ctx.Request.Headers.Authorization = "bearer abc123";

        Assert.Equal("abc123", AuthRequestPolicy.ExtractBearerOrQueryToken(ctx));
    }

    [Fact]
    public void PanelSession_AllowsPanelWidgetRoutes()
    {
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/ws")));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/lighting/animate/headless-start", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/cooling/profile/Balanced", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/api/media/spotify/album-art", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/api/obs/recording/toggle", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/preferences", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("GET", "/displays", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/displays/display1/brightness", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/system/volume", allowPanel: true)));
        Assert.True(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/system/volume/mute", allowPanel: true)));
    }

    [Fact]
    public async Task SystemRoutes_MarksVolumeWritesAsPanelAllowed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapSystemEndpoints();

        AssertPanelAllowedRoute(app, "GET", "/system/volume");
        AssertPanelAllowedRoute(app, "POST", "/system/volume");
        AssertPanelAllowedRoute(app, "POST", "/system/volume/mute");
    }

    [Fact]
    public async Task PanelRoutes_MarksServiceInfoAsPanelAllowed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapPanelEndpoints();

        AssertPanelAllowedRoute(app, "GET", "/panel/phone/service-info");
    }

    [Fact]
    public void PanelSession_BlocksAdminRoutes()
    {
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/shutdown")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/devices/update")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/profiles/import")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/media/import")));
        Assert.False(AuthRequestPolicy.IsPanelSessionAllowed(NewContext("POST", "/displays/display1/vcp/4")));
    }

    [Fact]
    public void SpaShellFallback_AllowsUnmatchedSameOriginHtmlNavigation()
    {
        var ctx = NewContext("GET", "/settings");
        ctx.Request.Headers.Accept = "text/html";
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        Assert.True(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void SpaShellFallback_BlocksMatchedApiRoutes()
    {
        var ctx = NewContext("GET", "/system/cpu/model");
        ctx.Request.Headers.Accept = "text/html";
        ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "system"));

        Assert.False(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void SpaShellFallback_AllowsCrossSiteHtmlNavigation()
    {
        // Android System WebView (and Chromium-based kiosks) send
        // `Sec-Fetch-Site: cross-site` on top-level navigations to a new
        // origin, even when the user originated the request - there is no
        // prior origin to compare against. Accepting that value is safe
        // for the SPA-shell GET path: it only returns index.html, not API
        // data. CSRF on state-changing endpoints is enforced separately by
        // RejectsInsecureCsrf.
        var ctx = NewContext("GET", "/settings");
        ctx.Request.Headers.Accept = "text/html";
        ctx.Request.Headers["Sec-Fetch-Site"] = "cross-site";

        Assert.True(AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_LetsHttpsTrafficThrough()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: true);
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_LetsSafeMethodsThroughOverHttp()
    {
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("GET", "/system/cpu", isHttps: false)));
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("HEAD", "/ping", isHttps: false)));
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(NewContext("OPTIONS", "/api/media", isHttps: false)));
    }

    [Fact]
    public void RejectsInsecureCsrf_AllowsSameOriginFetchOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_AllowsMatchingOriginHeaderOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Host = new HostString("192.168.1.235", 9400);
        ctx.Request.Headers.Origin = "http://192.168.1.235:9400";
        Assert.False(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksCrossSiteFetchOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksMismatchedOriginOverHttp()
    {
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        ctx.Request.Host = new HostString("192.168.1.235", 9400);
        ctx.Request.Headers.Origin = "http://malicious.lan";
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public void RejectsInsecureCsrf_BlocksMissingHeadersOverHttp()
    {
        // No Sec-Fetch-Site (older browser), no Origin (curl, scripted): on
        // a state-changing HTTP request we err on the side of rejecting.
        var ctx = NewContext("POST", "/cooling/profile/Balanced", isHttps: false);
        Assert.True(AuthRequestPolicy.RejectsInsecureCsrf(ctx));
    }

    [Fact]
    public async Task WellKnownPath_ServesWithoutToken()
    {
        // RFC 8615 discovery docs (apple-app-site-association) must be reachable
        // un-authenticated so Universal-Link verification can fetch them. The
        // public-path short-circuit runs before any DI resolution, so an empty
        // provider is enough to drive the real UseNexusPathAuth pipeline.
        var (reached, ctx) = await RunPathAuth("GET", "/.well-known/apple-app-site-association");

        Assert.True(reached);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    private static async Task<(bool reached, DefaultHttpContext ctx)> RunPathAuth(string method, string path)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        builder.UseNexusPathAuth();
        var reached = false;
        builder.Run(c =>
        {
            reached = true;
            c.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        var pipeline = builder.Build();

        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = "http";
        await pipeline(ctx);
        return (reached, ctx);
    }

    private static DefaultHttpContext NewContext(string method, string path, bool allowPanel = false, bool isHttps = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Scheme = isHttps ? "https" : "http";
        if (allowPanel)
        {
            ctx.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(AllowPanelAccess.Instance),
                "allow-panel"));
        }
        return ctx;
    }

    private static void AssertPanelAllowedRoute(WebApplication app, string method, string pattern)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

        Assert.NotNull(endpoint);
        Assert.NotNull(endpoint.Metadata.GetMetadata<AllowPanelAccess>());
    }
}
