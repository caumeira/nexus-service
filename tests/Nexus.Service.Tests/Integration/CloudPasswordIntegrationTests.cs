using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class CloudPasswordIntegrationTests : IClassFixture<CloudUsernameCooldownAppFactory>
{
    private readonly CloudUsernameCooldownAppFactory _factory;

    public CloudPasswordIntegrationTests(CloudUsernameCooldownAppFactory factory) => _factory = factory;

    private HttpClient SignedInClient()
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.RemoveAll(a => a.AccountId == "acct-1");
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        _factory.Api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Cloud_password_change_awaiting_email_confirmation_relays_202()
    {
        _factory.Api.OnChangePassword = (_, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance, 202);

        var res = await SignedInClient().PostAsJsonAsync("/cloud/password", new { newPassword = "NewPassw0rd" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_password_change_applied_upstream_answers_200()
    {
        _factory.Api.OnChangePassword = (_, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance, 204);

        var res = await SignedInClient().PostAsJsonAsync("/cloud/password", new { currentPassword = "Old", newPassword = "NewPassw0rd" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
