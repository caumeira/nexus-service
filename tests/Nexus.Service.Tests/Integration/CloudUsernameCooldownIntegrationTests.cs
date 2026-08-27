using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// tests' FakeCloudApiClient. CloudRoutesIntegrationTests exercises only the
/// logged-out paths (no way to reach a stubbed upstream response there); this
/// factory seeds an active session so /cloud/username can be driven against a
/// fake 409, proving the actual JSON envelope the dashboard receives.
/// </summary>
public sealed class CloudUsernameCooldownAppFactory : NexusAppFactory
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

public sealed class CloudUsernameCooldownIntegrationTests : IClassFixture<CloudUsernameCooldownAppFactory>
{
    private readonly CloudUsernameCooldownAppFactory _factory;

    public CloudUsernameCooldownIntegrationTests(CloudUsernameCooldownAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Cloud_username_change_cooldown_carries_retryAt_verbatim_in_the_local_response()
    {
        const string retryAt = "2026-07-03T12:00:00Z";
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        // nexus-api's 409 body: {code:'username_cooldown', message, retryAt}.
        _factory.Api.OnChangeUsername = (_, _) =>
            CloudApiResult<CloudVoid>.Fail(409, "username_cooldown", "Try again later.", retryAt);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);

        var res = await client.PostAsJsonAsync("/cloud/username", new { username = "newname" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("error").GetBoolean());
        Assert.Equal("username_cooldown", body.GetProperty("msg").GetString());
        Assert.Equal(retryAt, body.GetProperty("retryAt").GetString());
    }
}
