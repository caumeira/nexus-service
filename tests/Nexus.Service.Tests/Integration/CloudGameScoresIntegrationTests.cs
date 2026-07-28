using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Cloud;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// NexusAppFactory variant with ICloudApiClient swapped for the Cloud unit
/// tests' FakeCloudApiClient, same pattern as
/// CloudBenchmarkSubmitAppFactory - proves the raw forwarder's actual JSON
/// envelope and auth wiring without reaching the real cloud API.
/// </summary>
public sealed class CloudGameScoresAppFactory : NexusAppFactory
{
    public FakeCloudApiClient Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
        });
    }
}

[Collection("NexusHost")]
public sealed class CloudGameScoresIntegrationTests : IClassFixture<CloudGameScoresAppFactory>
{
    private readonly CloudGameScoresAppFactory _factory;

    public CloudGameScoresIntegrationTests(CloudGameScoresAppFactory factory) => _factory = factory;

    private System.Net.Http.HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Submit_without_desktop_token_is_401()
    {
        var res = await _factory.CreateClient().PostAsync("/cloud/games/scores",
            new System.Net.Http.StringContent("{\"gameType\":\"snake-easy\",\"score\":10}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task PanelSession_CanReachTheRoute()
    {
        // Defensive reset: shared factory/config store, see the note on
        // Submit_signed_out_forwards_without_bearer_and_relays_upstream_body_verbatim.
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.ActiveCloudAccountId = null;
        });
        _factory.Api.OnPostRaw = (path, _, _) =>
        {
            Assert.Equal("/games/scores", path);
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"best\":10,\"rank\":1,\"entries\":[]}", ContentType = "application/json" }, 201);
        };

        var res = await TestPhoneSession.CreateClient(_factory).PostAsync("/cloud/games/scores",
            new System.Net.Http.StringContent("{\"gameType\":\"snake-easy\",\"score\":10,\"durationMs\":5000}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode)201, res.StatusCode);
        Assert.Equal("{\"best\":10,\"rank\":1,\"entries\":[]}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_signed_out_forwards_without_bearer_and_relays_upstream_body_verbatim()
    {
        // Defensive reset: the factory (and its config store) is shared across
        // every test in this class, so this must not assume no earlier test
        // activated an account.
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.ActiveCloudAccountId = null;
        });

        string? capturedToken = "not-called";
        string? capturedBody = null;
        _factory.Api.OnPostRaw = (path, body, token) =>
        {
            capturedToken = token;
            capturedBody = body;
            Assert.Equal("/games/scores", path);
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"best\":10,\"rank\":3,\"entries\":[]}", ContentType = "application/json" }, 201);
        };

        var res = await DesktopClient().PostAsync("/cloud/games/scores",
            new System.Net.Http.StringContent("{\"gameType\":\"snake-easy\",\"score\":10,\"durationMs\":5000}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Null(capturedToken);
        Assert.Equal("{\"gameType\":\"snake-easy\",\"score\":10,\"durationMs\":5000}", capturedBody);
        Assert.Equal((HttpStatusCode)201, res.StatusCode);
        Assert.Equal("{\"best\":10,\"rank\":3,\"entries\":[]}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_signed_in_forwards_with_bearer_and_relays_upstream_status_verbatim()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-game", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-game";
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-game",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-game", Email = "x@example.com", Username = "x", EmailVerified = true },
        });

        string? capturedToken = null;
        _factory.Api.OnPostRaw = (_, _, token) =>
        {
            capturedToken = token;
            // A non-2xx upstream status (e.g. a validation rejection) must
            // still be relayed as-is, not translated into the local
            // ApiResponse envelope.
            return CloudApiResult<CloudRawResponse>.Ok(
                new CloudRawResponse { Body = "{\"code\":\"invalid_score\"}", ContentType = "application/json" }, 422);
        };

        var res = await DesktopClient().PostAsync("/cloud/games/scores",
            new System.Net.Http.StringContent("{\"gameType\":\"snake-easy\",\"score\":-1}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("access-game", capturedToken);
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal("{\"code\":\"invalid_score\"}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_too_large_body_is_rejected_without_forwarding()
    {
        var oversized = new string('a', 5 * 1024);
        _factory.Api.OnPostRaw = (_, _, _) =>
            throw new Xunit.Sdk.XunitException("oversized submission must not be forwarded upstream");

        var res = await DesktopClient().PostAsync("/cloud/games/scores",
            new System.Net.Http.StringContent("{\"gameType\":\"snake-easy\",\"padding\":\"" + oversized + "\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
