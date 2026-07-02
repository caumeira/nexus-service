using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Boots the real endpoint route table (see NexusAppFactory) to exercise
/// /cloud/... end to end through auth + routing, catching what a unit test
/// constructing CloudRoutes handlers directly never can.
///
/// MapDelete does NOT implicitly infer a JSON body parameter the way
/// MapPost/MapPut/MapPatch do - an unmarked complex-type parameter on a
/// MapDelete throws InvalidOperationException the first time a real Kestrel
/// listener realizes the endpoint table, taking every route in the process
/// down, not just /cloud/account ([FromBody] on CloudDeleteAccountBody in
/// CloudRoutes.cs opts in explicitly). Confirmed against a live
/// WebApplication.CreateSlimBuilder + Kestrel instance before the fix landed.
/// TestServer (what this factory uses) does NOT reproduce that crash - its
/// endpoint realization differs from real Kestrel for this case - so the
/// delete tests below exercise the route logically but are not, on their
/// own, a regression guard for this exact class of bug; the [FromBody]
/// annotation is.
/// </summary>
[Collection("NexusHost")]
public sealed class CloudRoutesIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public CloudRoutesIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    [Fact]
    public async Task Cloud_accounts_without_token_is_401()
    {
        var res = await _factory.CreateClient().GetAsync("/cloud/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_accounts_with_desktop_token_returns_empty_list_when_logged_out()
    {
        var res = await AuthedClient().GetAsync("/cloud/accounts");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task Cloud_sync_status_returns_idle_when_logged_out()
    {
        var res = await AuthedClient().GetAsync("/cloud/sync/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("idle", body.GetProperty("state").GetString());
    }

    // The route that would have thrown InvalidOperationException at endpoint
    // compilation before the [FromBody] fix - reaching ANY real HTTP status
    // (not an unhandled-exception 500 with the InferMetadata stack trace)
    // proves the route table itself is valid.
    [Fact]
    public async Task Cloud_account_delete_with_a_json_body_does_not_crash_route_compilation()
    {
        var client = AuthedClient();
        var req = new HttpRequestMessage(HttpMethod.Delete, "/cloud/account")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { currentPassword = "hunter2" }),
        };

        var res = await client.SendAsync(req);

        // No active session in this test host, so the route rejects with 401 -
        // the point is that it's a real, route-produced response, not a crash.
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_account_delete_without_a_body_does_not_crash_route_compilation()
    {
        var res = await AuthedClient().DeleteAsync("/cloud/account");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_login_with_bad_json_returns_a_client_error_not_a_crash()
    {
        var res = await AuthedClient().PostAsJsonAsync("/cloud/login", new { identifier = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Cloud_account_patch_without_token_is_401()
    {
        var res = await _factory.CreateClient().PatchAsJsonAsync("/cloud/account", new { isPrivate = true });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
