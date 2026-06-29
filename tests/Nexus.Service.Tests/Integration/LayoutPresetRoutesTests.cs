using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

[Collection("NexusHost")]
public sealed class LayoutPresetRoutesTests : IDisposable
{
    private readonly NexusAppFactory _factory;
    private readonly HttpClient _client;

    public LayoutPresetRoutesTests()
    {
        _factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ILightingDeviceProvider>();
                s.AddSingleton<ILightingDeviceProvider>(sp =>
                    new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
            })) as NexusAppFactory ?? new NexusAppFactory();

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose() => _factory.Dispose();

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store =>
        _factory.Services.GetRequiredService<IConfigStore>();

    // ---- GET ----

    [Fact]
    public async Task Get_returns_empty_list_and_null_activeId_on_fresh_store()
    {
        var res = await _client.GetAsync("/devices/lighting-devices/layout-presets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("presets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("activeId").ValueKind);
    }

    // ---- POST create ----

    [Fact]
    public async Task Create_appends_preset_and_sets_activeId()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-1"] = new DeviceLayout { X = 5, Y = 10, W = 80, H = 40 };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Gaming"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var preset = doc.RootElement.GetProperty("preset");
        var id = preset.GetProperty("id").GetString();
        Assert.Equal("Gaming", preset.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(id, doc.RootElement.GetProperty("activeId").GetString());

        var stored = Store.Load();
        Assert.Single(stored.Lighting.LayoutPresets, p => p.Id == id);
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        Assert.True(stored.Lighting.LayoutPresets[0].Layouts.ContainsKey("dev-1"));
    }

    [Fact]
    public async Task Create_cap_returns_400_when_at_10_presets()
    {
        for (var i = 0; i < 10; i++)
        {
            var r = await _client.PostAsync(
                "/devices/lighting-devices/layout-presets",
                Json("{\"name\":\"Preset " + i + "\"}"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Overflow"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(10, Store.Load().Lighting.LayoutPresets.Count);
    }

    // ---- PUT update ----

    [Fact]
    public async Task Put_saveCurrent_overwrites_preset_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-a"] = new DeviceLayout { X = 1, Y = 2, W = 50, H = 30 };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Original"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // Change the live layouts before overwriting the preset.
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-b"] = new DeviceLayout { X = 9, Y = 9, W = 80, H = 40 };
        });

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        var p = stored.Lighting.LayoutPresets.Find(x => x.Id == id)!;
        Assert.True(p.Layouts.ContainsKey("dev-b"));
    }

    [Fact]
    public async Task Put_name_renames_preset_only()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Old Name"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"name":"New Name","saveCurrent":false}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        Assert.Equal("New Name", stored.Lighting.LayoutPresets[0].Name);
    }

    [Fact]
    public async Task Put_returns_404_for_unknown_id()
    {
        var res = await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/nonexistent",
            Json("""{"name":"X"}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_active_preset_nulls_activeId()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        var res = await _client.DeleteAsync($"/devices/lighting-devices/layout-presets/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activeId").ValueKind);

        var stored = Store.Load();
        Assert.Empty(stored.Lighting.LayoutPresets);
        Assert.Null(stored.Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public async Task Delete_non_active_preset_leaves_activeId()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P2"}"""));

        var presets = Store.Load().Lighting.LayoutPresets;
        var firstId = presets[0].Id;
        var secondId = presets[1].Id;

        // Activate the second preset, then delete the first.
        await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/active",
            Json("{\"id\":\"" + secondId + "\"}"));
        var res = await _client.DeleteAsync($"/devices/lighting-devices/layout-presets/{firstId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(secondId, doc.RootElement.GetProperty("activeId").GetString());

        var stored = Store.Load();
        Assert.Single(stored.Lighting.LayoutPresets);
        Assert.Equal(secondId, stored.Lighting.ActiveLayoutPresetId);
    }

    // ---- PUT /active ----

    [Fact]
    public async Task Put_active_sets_id_without_changing_device_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["dev-x"] = new DeviceLayout { X = 3, Y = 4, W = 60, H = 30 };
        });
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        // Clear active then set it back via /active.
        Store.Update(s => s.Lighting.ActiveLayoutPresetId = null);

        var res = await _client.PutAsync(
            "/devices/lighting-devices/layout-presets/active",
            Json("{\"id\":\"" + id + "\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        // DeviceLayouts must not have changed.
        Assert.True(stored.Lighting.DeviceLayouts.ContainsKey("dev-x"));
    }

    // ---- POST /layouts (batch apply) ----

    [Fact]
    public async Task Post_layouts_replaces_device_layouts()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["old-dev"] = new DeviceLayout { X = 1, Y = 1, W = 50, H = 20 };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layouts",
            Json("""{"layouts":{"new-dev":{"x":7,"y":8,"w":60,"h":30,"rotation":0}}}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.False(stored.Lighting.DeviceLayouts.ContainsKey("old-dev"));
        Assert.True(stored.Lighting.DeviceLayouts.TryGetValue("new-dev", out var layout));
        Assert.Equal(7f, layout.X);
        Assert.Equal(8f, layout.Y);
        Assert.Equal(60f, layout.W);
    }

    // ---- POST /{id}/activate ----

    [Fact]
    public async Task Activate_copies_preset_layouts_and_sets_activeId()
    {
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts["original"] = new DeviceLayout { X = 1, Y = 1, W = 50, H = 20 };
        });
        await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"Saved"}"""));
        var id = Store.Load().Lighting.LayoutPresets[0].Id;

        // Change live layouts.
        Store.Update(s =>
        {
            s.Lighting.DeviceLayouts.Clear();
            s.Lighting.DeviceLayouts["changed"] = new DeviceLayout { X = 9, Y = 9, W = 80, H = 40 };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(id, stored.Lighting.ActiveLayoutPresetId);
        Assert.True(stored.Lighting.DeviceLayouts.ContainsKey("original"));
        Assert.False(stored.Lighting.DeviceLayouts.ContainsKey("changed"));
    }

    [Fact]
    public async Task Activate_returns_404_for_unknown_id()
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/nonexistent/activate",
            null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
