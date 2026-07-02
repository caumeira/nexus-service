using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// /profiles/create, /profiles/{id}/rename, and /profiles/import over the
/// real request pipeline: a name collision with a DIFFERENT profile must
/// reach the client as 409 with msg "profile_name_taken" (nexus-web maps
/// this exact string to a translated inline error), not a generic 400. Own
/// <see cref="NexusAppFactory"/> per test (not a shared IClassFixture) since
/// profile names are state within one factory's ProfileManager and would leak
/// across test methods sharing that factory otherwise.
/// </summary>
[Collection("NexusHost")]
public sealed class ProfileRoutesIntegrationTests : IDisposable
{
    private readonly NexusAppFactory _factory;

    public ProfileRoutesIntegrationTests()
    {
        _factory = new NexusAppFactory();
        // AppBootstrap.InitializeProfiles is skipped for the test host (Program.cs
        // gates it behind !testHost), so the Default profile is never seeded.
        _factory.Services.GetRequiredService<ProfileManager>().Initialize();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> DefaultProfileIdAsync(HttpClient client)
    {
        var res = await client.GetAsync("/profiles");
        var list = await res.Content.ReadFromJsonAsync<JsonElement>();
        return list.GetProperty("profiles").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "Default")
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        var first = await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var res = await client.PostAsJsonAsync("/profiles/create", new { name = "  GAMING  " });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Rename_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        var defaultId = await DefaultProfileIdAsync(client);

        var res = await client.PostAsJsonAsync($"/profiles/{defaultId}/rename", new { name = "gaming" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Rename_to_its_own_current_name_succeeds()
    {
        var client = AuthedClient();
        var defaultId = await DefaultProfileIdAsync(client);

        var res = await client.PostAsJsonAsync($"/profiles/{defaultId}/rename", new { name = "DEFAULT" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEFAULT", body.GetProperty("profile").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Import_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        var defaultId = await DefaultProfileIdAsync(client);

        var exportRes = await client.GetAsync($"/profiles/{defaultId}/export");
        using var exportDoc = JsonDocument.Parse(await exportRes.Content.ReadAsStringAsync());
        var settingsJson = exportDoc.RootElement.GetProperty("settings").GetRawText();
        var importBody = $$"""{"name":"Gaming","settings":{{settingsJson}}}""";

        var res = await client.PostAsync("/profiles/import",
            new StringContent(importBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Import_with_a_distinct_name_succeeds()
    {
        var client = AuthedClient();
        var defaultId = await DefaultProfileIdAsync(client);

        var exportRes = await client.GetAsync($"/profiles/{defaultId}/export");
        using var exportDoc = JsonDocument.Parse(await exportRes.Content.ReadAsStringAsync());
        var settingsJson = exportDoc.RootElement.GetProperty("settings").GetRawText();
        var importBody = $$"""{"name":"Imported Copy","settings":{{settingsJson}}}""";

        var res = await client.PostAsync("/profiles/import",
            new StringContent(importBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Imported Copy", body.GetProperty("profile").GetProperty("name").GetString());
    }
}
