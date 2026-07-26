using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>POST /telemetry/consent: crash-safe marker-before-flip ordering and the not-a-transition welcome confirm, with a fake IFleetEventTransport that always fails so the pending marker stays observable; delivery-success clearing is covered in FleetEventServiceTests.</summary>
[Collection("NexusHost")]
public sealed class TelemetryConsentTransitionTests : IDisposable
{
    private sealed class FakeFleetEventTransport : IFleetEventTransport
    {
        public Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct) => Task.FromResult(false);
    }

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public TelemetryConsentTransitionTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFleetEventTransport>();
                services.AddSingleton<IFleetEventTransport, FakeFleetEventTransport>();
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> PostConsent(HttpClient client, bool enabled) =>
        client.PostAsJsonAsync("/telemetry/consent", new TelemetryConsentBody { Enabled = enabled });

    [Fact]
    public async Task Welcome_confirm_of_the_fresh_install_default_is_not_a_transition()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.True(store.Load().Telemetry.CollectAnonymousData); // fresh-install default

        var res = await PostConsent(DesktopClient(), true);

        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
        Assert.True(store.Load().Telemetry.CollectAnonymousData);
    }

    [Fact]
    public async Task Opting_out_persists_the_pending_event_before_flipping_the_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.True(store.Load().Telemetry.CollectAnonymousData); // fresh-install default
        // FleetTelemetryWorker mints this at boot in production; NexusAppFactory strips hosted services, so seed it directly.
        store.Update(s => s.Telemetry.InstallId = "test-install-id");

        var res = await PostConsent(DesktopClient(), false);

        Assert.True(res.IsSuccessStatusCode);
        Assert.False(store.Load().Telemetry.CollectAnonymousData);
        Assert.Equal(TelemetryEvents.OptOut, store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task Opting_back_in_after_opting_out_is_a_real_transition()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Telemetry.InstallId = "test-install-id");
        var client = DesktopClient();
        Assert.True((await PostConsent(client, false)).IsSuccessStatusCode);
        Assert.False(store.Load().Telemetry.CollectAnonymousData);

        var res = await PostConsent(client, true);

        Assert.True(res.IsSuccessStatusCode);
        Assert.True(store.Load().Telemetry.CollectAnonymousData);
        Assert.Equal(TelemetryEvents.OptIn, store.Load().Telemetry.FleetPendingConsentEvent);
    }
}
