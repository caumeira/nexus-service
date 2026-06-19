using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests;

/// <summary>
/// Guards the local service against the ASUS DriverHub class of bug
/// (CVE-2025-3462/3463): a public web page talking to a localhost service
/// where a substring origin check let a look-alike host
/// (driverhub.asus.com.attacker.com) impersonate the trusted origin.
///
/// These run the real production CORS policy (built by
/// <see cref="CorsConfig.AddNexusCors"/>) through the real ASP.NET Core
/// <see cref="ICorsService"/> matcher. No network, no DNS, no host boot:
/// the request Origin is just a header string and matching is in-process. If
/// anyone swaps <c>WithOrigins</c> for a <c>SetIsOriginAllowed</c> substring/
/// Contains/EndsWith predicate, the look-alike cases below start passing and
/// these tests fail.
/// </summary>
public class CorsConfigTests
{
    // Canonical production allowlist shape: loopback + the hosted web app.
    private static readonly string[] Origins =
    {
        "http://localhost:9400",
        "http://127.0.0.1:9400",
        "https://hellonexus.com",
        "https://www.hellonexus.com",
    };

    // Runs a request Origin through the production-built default policy and the
    // real ASP.NET Core matcher. IsOriginAllowed is the authoritative allow/deny
    // decision (the middleware only emits Access-Control-Allow-Origin when it is
    // true); AllowedOrigin is informational and, in this code path, echoes the
    // request origin regardless - so deny is asserted via IsOriginAllowed.
    private static async Task<bool> IsOriginAllowed(
        string[] allowedOrigins, bool debugLoopbackWildcard, string requestOrigin)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        CorsConfig.AddNexusCors(services, allowedOrigins, debugLoopbackWildcard);
        await using var sp = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Headers.Origin = requestOrigin;

        var policy = await sp.GetRequiredService<ICorsPolicyProvider>()
            .GetPolicyAsync(ctx, policyName: null);
        Assert.NotNull(policy);

        var result = sp.GetRequiredService<ICorsService>().EvaluatePolicy(ctx, policy!);
        return result.IsOriginAllowed;
    }

    [Theory]
    [InlineData("https://hellonexus.com")]
    [InlineData("https://www.hellonexus.com")]
    [InlineData("http://localhost:9400")]
    [InlineData("http://127.0.0.1:9400")]
    public async Task Release_AllowsExactAllowlistedOrigins(string origin)
    {
        Assert.True(await IsOriginAllowed(Origins, debugLoopbackWildcard: false, origin));
    }

    [Theory]
    [InlineData("https://hellonexus.com.attacker.com")] // suffix attack - the ASUS bug
    [InlineData("https://hellonexus.com.evil.com")]
    [InlineData("https://evilhellonexus.com")]          // prefix attack
    [InlineData("https://hellonexus.com.au")]           // extension attack
    [InlineData("https://hellonexus.evil.com")]
    [InlineData("http://hellonexus.com")]               // downgraded scheme
    [InlineData("https://attacker.com")]
    [InlineData("null")]                                // sandboxed / file origin
    public async Task Release_RejectsLookalikeOrigins(string origin)
    {
        Assert.False(await IsOriginAllowed(Origins, debugLoopbackWildcard: false, origin));
    }

    [Fact]
    public async Task DebugLoopbackWildcard_AcceptsLoopbackButNotLookalikes()
    {
        // The dev policy accepts any loopback port (Vite on 5173-5180, etc.)...
        Assert.True(await IsOriginAllowed(Origins, debugLoopbackWildcard: true, "http://localhost:5173"));
        Assert.True(await IsOriginAllowed(Origins, debugLoopbackWildcard: true, "http://127.0.0.1:5180"));

        // ...but the ":" port separator is load-bearing: a host that merely
        // starts with "http://localhost" is NOT loopback and must be rejected,
        // and the public allowlist look-alike must never ride the dev policy.
        Assert.False(await IsOriginAllowed(Origins, debugLoopbackWildcard: true, "http://localhost.attacker.com"));
        Assert.False(await IsOriginAllowed(Origins, debugLoopbackWildcard: true, "http://127.0.0.1.attacker.com"));
        Assert.False(await IsOriginAllowed(Origins, debugLoopbackWildcard: true, "https://hellonexus.com.attacker.com"));
    }

    [Fact]
    public void BuildAllowedOrigins_IncludesPublicAppAndNeverWildcards()
    {
        var origins = CorsConfig.BuildAllowedOrigins(9400, 9443);

        Assert.Contains("https://hellonexus.com", origins);
        Assert.Contains("https://www.hellonexus.com", origins);
        Assert.Contains("http://localhost:9400", origins);
        Assert.Contains("http://127.0.0.1:9400", origins);
        Assert.Contains("https://localhost:9443", origins);

        // A wildcard or scheme-relative entry would defeat exact matching.
        Assert.DoesNotContain(origins, o => o.Contains('*'));
        Assert.All(origins, o => Assert.True(
            Uri.TryCreate(o, UriKind.Absolute, out _), $"not an absolute origin: {o}"));
    }
}
