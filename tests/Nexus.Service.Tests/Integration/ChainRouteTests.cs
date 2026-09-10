using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Models;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The assign-a-device-to-an-ARGB-port flow over the real routes: the offline
/// product picker, wiring a chain to a port, and what the editor reads back.
/// Unit tests cover the resolution rules; these cover the wire, which is where
/// a missing AppJsonContext entry or a route-level guard actually bites.
/// </summary>
public sealed class ChainRouteTests : IDisposable
{
    private const string PortId = "fakeport:1";

    private const int DefaultLedCount = 60;

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// A single addressable ARGB port: one resizable segment, nothing else.
    /// The segment tracks the persisted count, as every real port provider
    /// does - a fixture pinned to the firmware number would make every
    /// partition fail to tile and self-heal back to defaults.
    /// </summary>
    private sealed class FakePortSource(IConfigStore store) : IDeviceStructureSource
    {
        public IReadOnlyList<DeviceStructure> GetStructures()
        {
            var ledCount = store.Load().Devices.ZoneLedCounts.TryGetValue(PortId, out var persisted)
                ? persisted
                : DefaultLedCount;
            var s = new DeviceStructure { DeviceId = PortId, Name = "Fake Port", Partitionable = true };
            s.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = "ARGB",
                LedCount = ledCount,
                FrameLedCount = ledCount,
                Resizable = true,
                ZoneType = "linear",
            });
            s.DefaultZones.Add(new DefaultZoneDef
            {
                Id = PortId,
                Name = "Fake Port",
                RawName = "ARGB",
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
            });
            return new[] { s };
        }
    }

    public ChainRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        // Every read must go through THIS host: WithWebHostBuilder boots a
        // second one, and its IConfigStore caches its own copy of settings.
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IDeviceStructureSource>(sp => new FakePortSource(sp.GetRequiredService<IConfigStore>()))));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private static string Catalog(string? q = null, int limit = 50)
        => $"/devices/lighting-devices/mappings/catalog?limit={limit}" + (q is null ? "" : $"&q={q}");

    private Task<HttpResponseMessage> PostChain(params SetChainEntry[] entries)
        => _client.PostAsJsonAsync($"/devices/lighting-devices/{PortId}/mappings/chain",
            new SetChainBody { Entries = entries.ToList() });

    [Fact]
    public async Task The_picker_offers_the_generics_first_and_reaches_the_network_for_nothing()
    {
        var resp = await _client.GetFromJsonAsync<BuiltInMappingsResponse>(Catalog());

        Assert.NotNull(resp);
        Assert.Equal(GenericChainArtifacts.FanKey, resp!.Items[0].Key);
        Assert.Equal(GenericChainArtifacts.StripKey, resp.Items[1].Key);
        Assert.True(resp.Items[0].Parametric);
        // Total counts what matched, which includes the rows that are not in
        // the packed file at all.
        Assert.True(resp.Total > BuiltInMappingsCatalog.All.Count);
    }

    [Fact]
    public async Task Our_own_accessories_are_in_the_picker_under_the_brand()
    {
        var resp = await _client.GetFromJsonAsync<BuiltInMappingsResponse>(Catalog("HYTE"));

        Assert.NotNull(resp);
        var byKey = resp!.Items.ToDictionary(i => i.Key);
        Assert.Equal(HyteChainArtifacts.All.Count, resp.Total);
        foreach (var product in HyteChainArtifacts.All)
        {
            Assert.True(byKey.ContainsKey(product.Key), product.Key);
            Assert.Equal(product.LedCount, byKey[product.Key].LedCount);
            Assert.Equal("HYTE", byKey[product.Key].Brand);
            Assert.False(byKey[product.Key].Parametric);
        }
    }

    [Fact]
    public async Task An_unchained_port_reads_back_as_one_editable_row()
    {
        var resp = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");

        Assert.NotNull(resp);
        Assert.True(resp!.Chainable);
        Assert.True(resp.IsDefaultPartition);
        var only = Assert.Single(resp.Chain);
        // A port with nothing declared reads as a generic strip the user can
        // resize, so the editor renders the same list either way.
        Assert.Equal(GenericChainArtifacts.StripKey, only.Key);
        Assert.Equal(60, only.LedCount);
        Assert.True(only.EditableCount);
    }

    [Fact]
    public async Task Wiring_a_chain_turns_one_port_into_one_card_per_product()
    {
        var post = await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12-trio" },
            new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 20 },
            new SetChainEntry { Key = "product:hyte-y50-solo" });

        post.EnsureSuccessStatusCode();
        var result = await post.Content.ReadFromJsonAsync<SetChainResponse>();
        Assert.NotNull(result);
        Assert.False(result!.Error);
        Assert.Equal(68 + 20 + 8, result.LedCount);
        Assert.Equal(new[] { $"{PortId}:z0", $"{PortId}:z1", $"{PortId}:z2" }, result.ZoneIds.ToArray());

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");
        Assert.NotNull(structure);
        Assert.False(structure!.IsDefaultPartition);
        Assert.Equal(3, structure.Chain.Count);
        // No ordinal suffix: three identical fans read as three of the same
        // product, and their position in the chain is what tells them apart.
        Assert.Equal(new[] { "FR12 Trio", "Generic Strip", "Y50 Solo Fan" },
            structure.Chain.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 68, 20, 8 }, structure.Chain.Select(c => c.LedCount).ToArray());
        // A product fixes its own count; only the generic's is the user's.
        Assert.Equal(new[] { false, true, false }, structure.Chain.Select(c => c.EditableCount).ToArray());
        Assert.Equal(96, structure.Segments[0].LedCount);
    }

    [Fact]
    public async Task Each_product_lands_on_its_own_zone_with_its_own_mapping()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-trio" })).EnsureSuccessStatusCode();

        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        var applied = settings.Devices.AppliedMappings;

        Assert.Equal("product:hyte-fr12", applied[$"{PortId}:z0"].MappingId);
        Assert.Equal("product:hyte-y50-trio", applied[$"{PortId}:z1"].MappingId);
        // The geometry rides along, so the canvas has the rings without ever
        // asking the registry for them.
        Assert.Equal(33, applied[$"{PortId}:z0"].Artifact.Zones[0].Leds.Count);
        Assert.Equal(24, applied[$"{PortId}:z1"].Artifact.Zones[0].Leds.Count);
    }

    [Fact]
    public async Task Clearing_the_chain_returns_the_port_to_one_zone()
    {
        (await PostChain(new SetChainEntry { Key = "product:hyte-fr12" })).EnsureSuccessStatusCode();
        (await PostChain()).EnsureSuccessStatusCode();

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");

        Assert.NotNull(structure);
        Assert.True(structure!.IsDefaultPartition);
        Assert.Single(structure.Chain);
        // The declared total stays; the user has only stopped naming the parts.
        Assert.Equal(33, structure.Chain[0].LedCount);
    }

    [Fact]
    public async Task Resetting_the_zones_forgets_the_chain_too()
    {
        (await PostChain(
            new SetChainEntry { Key = "product:hyte-fr12" },
            new SetChainEntry { Key = "product:hyte-y50-solo" })).EnsureSuccessStatusCode();

        var reset = await _client.DeleteAsync($"/devices/lighting-devices/{PortId}/zones");
        reset.EnsureSuccessStatusCode();

        var structure = await _client.GetFromJsonAsync<DeviceStructureResponse>(
            $"/devices/lighting-devices/{PortId}/structure");
        Assert.NotNull(structure);
        Assert.True(structure!.IsDefaultPartition);
        // A chain record left behind would keep naming products the zones no
        // longer match, and keep rule 2 lifted for a segment nothing owns.
        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.DoesNotContain(ZoneResolution.ChainKey(PortId, 0), settings.Devices.PortChains.Keys);
    }

    [Fact]
    public async Task An_unknown_product_is_refused_rather_than_silently_dropped()
    {
        var post = await PostChain(new SetChainEntry { Key = "product:does-not-exist" });
        var body = await post.Content.ReadFromJsonAsync<ApiResponse>();

        Assert.NotNull(body);
        Assert.True(body!.Error);
    }

    [Fact]
    public async Task A_chain_longer_than_a_port_can_carry_is_refused()
    {
        // Six generics at the per-link maximum: each is legal on its own, and
        // the sum is what has to be caught.
        var links = Enumerable.Range(0, 6)
            .Select(_ => new SetChainEntry { Key = GenericChainArtifacts.StripKey, LedCount = 4096 })
            .ToArray();

        var post = await PostChain(links);
        var body = await post.Content.ReadFromJsonAsync<ApiResponse>();

        Assert.NotNull(body);
        Assert.True(body!.Error);
        var settings = _factory.Services.GetRequiredService<IConfigStore>().Load();
        Assert.DoesNotContain(ZoneResolution.ChainKey(PortId, 0), settings.Devices.PortChains.Keys);
    }

    [Fact]
    public async Task A_generic_needs_a_count_and_a_product_ignores_one()
    {
        var noCount = await PostChain(new SetChainEntry { Key = GenericChainArtifacts.FanKey });
        Assert.True((await noCount.Content.ReadFromJsonAsync<ApiResponse>())!.Error);

        // A client cannot resize a product: its artifact decides.
        var post = await PostChain(new SetChainEntry { Key = "product:hyte-fr12", LedCount = 999 });
        post.EnsureSuccessStatusCode();
        var result = await post.Content.ReadFromJsonAsync<SetChainResponse>();
        Assert.Equal(33, result!.LedCount);
    }
}
