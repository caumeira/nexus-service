using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cloud;
using Nexus.Service.Common;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Common;

/// <summary>
/// The baked token is a compile-time const, so which path the parameterless
/// Apply takes depends on whether the machine building the tests has a
/// credential. Only the explicit-token overload can assert both directions.
/// </summary>
public sealed class ClientCredentialTests
{
    [Fact]
    public void Default_apply_follows_the_baked_credential()
    {
        using var client = new HttpClient();

        ClientCredential.Apply(client);

        Assert.Equal(
            ClientCredential.IsOfficial,
            client.DefaultRequestHeaders.Contains(ClientCredential.HeaderName));
    }

    [Fact]
    public void Official_build_stamps_the_token()
    {
        using var client = new HttpClient();

        ClientCredential.Apply(client, "eyJ2IjoiMi40LjEifQ.SIG");

        Assert.Equal(
            "eyJ2IjoiMi40LjEifQ.SIG",
            Assert.Single(client.DefaultRequestHeaders.GetValues(ClientCredential.HeaderName)));
    }

    [Fact]
    public async Task Offline_cloud_client_never_reaches_the_network()
    {
        var api = new OfflineCloudApiClient();

        var login = await api.LoginAsync(new CloudLoginRequest(), CancellationToken.None);
        var profiles = await api.ListProfilesAsync("token", CancellationToken.None);

        Assert.True(login.Offline);
        Assert.False(login.Success);
        Assert.True(profiles.Offline);
    }

    [Fact]
    public async Task Null_fleet_transport_reports_failure_so_nothing_is_marked_delivered()
    {
        var transport = new NullFleetEventTransport();

        Assert.False(await transport.SendAsync(new FleetEventPayload(), CancellationToken.None));
    }

    [Fact]
    public void Empty_token_is_omitted_rather_than_sent_blank()
    {
        using var client = new HttpClient();

        ClientCredential.Apply(client, "");

        Assert.False(client.DefaultRequestHeaders.Contains(ClientCredential.HeaderName));
    }
}
