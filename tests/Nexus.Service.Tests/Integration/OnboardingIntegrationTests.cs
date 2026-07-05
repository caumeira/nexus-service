using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the real /onboarding routes through Program.cs. LocalhostOnly +
/// token-gated, so requests set a loopback RemoteIpAddress (as the
/// auth-middleware tests do). Effect is asserted via the shared IConfigStore
/// rather than the response body. Split into two classes (read-only vs the
/// one mutating test) because IClassFixture shares a single NexusAppFactory
/// across every test method in a class, and xUnit does not order them.
/// </summary>
[Collection("NexusHost")]
public sealed class OnboardingIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private async Task<int> Send(string method, string path, bool withToken = true)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
            {
                c.Request.Headers.Authorization = "Bearer " + Token;
            }
        });
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Status_is_reachable_on_loopback_with_token_and_defaults_incomplete()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().OnboardingCompleted);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", "/onboarding"));
    }

    [Fact]
    public async Task Status_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", "/onboarding", withToken: false));

    [Fact]
    public async Task Complete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/complete", withToken: false));
}

[Collection("NexusHost")]
public sealed class OnboardingCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Complete_sets_the_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().OnboardingCompleted);
    }
}
