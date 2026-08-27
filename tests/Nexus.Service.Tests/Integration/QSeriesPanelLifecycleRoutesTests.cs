using System.Net;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Route-level gating for the NEX-14 panel lifecycle endpoints. Both are
/// destructive and both refuse rather than act when their preconditions do not
/// hold, so the refusal branches are the contract worth pinning; the adb work
/// behind them is covered by ApkFlasherTests, and their panel-denial by
/// AuthRequestPolicyTests.
/// </summary>
public sealed class QSeriesPanelLifecycleRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public QSeriesPanelLifecycleRoutesTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Empty() => new("{}", Encoding.UTF8, "application/json");

    private PanelDeviceRegistry Registry => _factory.Services.GetRequiredService<PanelDeviceRegistry>();

    private string NewDevice(string surface)
    {
        var record = Registry.Allocate(null, new PanelDeviceCapabilities { Surface = surface });
        return record.Id;
    }

    // ── factory reset ──

    [Fact]
    public async Task FactoryReset_requires_auth()
    {
        var res = await _factory.CreateClient()
            .PostAsync("/panel/devices/whatever/factory-reset", Empty());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task FactoryReset_404s_an_unknown_device()
    {
        var res = await AuthedClient()
            .PostAsync("/panel/devices/no-such-device/factory-reset", Empty());
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task FactoryReset_refuses_a_non_qseries_surface()
    {
        // Only the Q-series runs an APK Nexus installs; a phone or monitor
        // record has no panel software to reinstall.
        var id = NewDevice(PanelSurfaces.Phone);
        var res = await AuthedClient().PostAsync($"/panel/devices/{id}/factory-reset", Empty());

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("Q-series", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryReset_leaves_settings_intact_when_no_panel_is_attached()
    {
        // The flash is started before the host-side wipe precisely so a refusal
        // costs the user nothing. No panel is registered in this host, so the
        // flasher rejects and the record must come through untouched.
        var id = NewDevice(PanelSurfaces.Q60);
        var before = Registry.Get(id);
        Assert.NotNull(before);
        Registry.Patch(id, new PanelDevicePatch { DisplayName = "keep me", AccentColor = "#abcdef" });

        var res = await AuthedClient().PostAsync($"/panel/devices/{id}/factory-reset", Empty());

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var after = Registry.Get(id);
        Assert.Equal("keep me", after?.DisplayName);
        Assert.Equal("#abcdef", after?.AccentColor);
    }

    [Fact]
    public async Task FactoryReset_refuses_while_another_flash_holds_the_gate()
    {
        var gate = _factory.Services.GetRequiredService<FlashGate>();
        var id = NewDevice(PanelSurfaces.Q60);
        Assert.True(gate.TryAcquire(out _));
        try
        {
            var res = await AuthedClient().PostAsync($"/panel/devices/{id}/factory-reset", Empty());
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
        finally
        {
            gate.Release();
        }
    }

    // ── reboot ──

    [Fact]
    public async Task Reboot_refuses_when_no_panel_is_attached()
    {
        var res = await AuthedClient().PostAsync("/devices/qseries/reboot", Empty());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Reboot_refuses_while_a_flash_holds_the_gate()
    {
        // A reboot queued mid-flash would abort the uninstall or leave the panel
        // unpinned from HOME, so the gate covers the whole flash, not just the
        // install span.
        var gate = _factory.Services.GetRequiredService<FlashGate>();
        Assert.True(gate.TryAcquire(out _));
        try
        {
            var res = await AuthedClient().PostAsync("/devices/qseries/reboot", Empty());
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
        finally
        {
            gate.Release();
        }
    }

    // ── panel reachability ──

    private Microsoft.AspNetCore.Routing.RouteEndpoint? Endpoint(string method, string pattern) =>
        _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .FirstOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, pattern, StringComparison.Ordinal)
                && (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

    [Theory]
    [InlineData("/devices/qseries/reboot")]
    [InlineData("/panel/devices/{id}/factory-reset")]
    public void Destructive_panel_actions_are_not_reachable_from_a_panel_session(string pattern)
    {
        // Panel-reachable routes opt in with .AllowPanel(). A panel must not be
        // able to reboot or factory-reset itself.
        var endpoint = Endpoint("POST", pattern);
        Assert.NotNull(endpoint);
        Assert.Null(endpoint!.Metadata.GetMetadata<AllowPanelAccess>());
    }
}
