using System.Net;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Smoke test proving the WebApplicationFactory harness boots the real host
/// in-process and routes a request end-to-end through the production pipeline.
/// </summary>
[Collection("NexusHost")]
public sealed class IntegrationHarnessTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public IntegrationHarnessTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Host_boots_and_serves_public_ping()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/ping");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
