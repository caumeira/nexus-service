using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the real /telemetry/consent route through Program.cs. It's
/// LocalhostOnly + token-gated, so requests set a loopback RemoteIpAddress
/// (as the auth-middleware tests do). Effect is asserted via the shared
/// IConfigStore rather than the response body.
/// </summary>
[Collection("NexusHost")]
public sealed class TelemetryConsentIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public TelemetryConsentIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private async Task<int> Send(string method, string? jsonBody, bool withToken = true)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = "/telemetry/consent";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
                c.Request.Headers.Authorization = "Bearer " + Token;
            if (jsonBody is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(jsonBody);
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bytes);
                c.Request.ContentLength = bytes.Length;
            }
        });
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Consent_is_reachable_on_loopback_with_token_and_defaults_off()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().Telemetry.CollectAnonymousData);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", null));
    }

    [Fact]
    public async Task Consent_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", null, withToken: false));
}
