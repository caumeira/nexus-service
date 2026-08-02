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

    // ---- power state capture and restore ----

    [Fact]
    public async Task Create_captures_disabled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-a", "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"WithPower"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        var preset = stored.Lighting.LayoutPresets[0];
        Assert.Equal(new List<string> { "dev-a", "dev-b" }, preset.DisabledDevices);
    }

    [Fact]
    public async Task SaveCurrent_captures_disabled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-x" };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-y", "dev-z" };
        });

        var putRes = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var stored = Store.Load();
        var preset = stored.Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-y", "dev-z" }, preset.DisabledDevices);
    }

    [Fact]
    public async Task Activate_restores_disabled_devices_from_preset()
    {
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-a" };
        });
        var createRes = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json("""{"name":"P1"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

        // Change global disabled list after the preset was saved.
        Store.Update(s =>
        {
            s.Devices.DisabledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(new List<string> { "dev-a" }, stored.Devices.DisabledLightingDevices);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_power_leaves_global_disabled_untouched()
    {
        // A preset saved before per-preset power has DisabledDevices = null;
        // activating it must not wipe the user's current global disabled list.
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
            s.Devices.DisabledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal(new List<string> { "dev-b" }, stored.Devices.DisabledLightingDevices);
    }

    // ---- ignore (uncontrolled) state capture and restore ----

    [Fact]
    public async Task Create_captures_uncontrolled_devices_into_preset()
    {
        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-ignored" };
        });

        var id = await CreatePreset("Ignoring");

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-ignored" }, preset.UncontrolledDevices);
    }

    [Fact]
    public async Task Activate_restores_uncontrolled_devices_from_preset()
    {
        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-a" };
        });
        var id = await CreatePreset("P1");

        Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(new List<string> { "dev-a" }, Store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_ignore_state_leaves_the_live_list_untouched()
    {
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
            s.Devices.UncontrolledLightingDevices = new List<string> { "dev-b" };
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(new List<string> { "dev-b" }, Store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public async Task Toggling_ignore_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/devices/lighting-devices/controlled",
            Json("""{"id":"dev-x","controlled":false}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(new List<string> { "dev-x" }, preset.UncontrolledDevices);
    }

    [Fact]
    public async Task Toggling_power_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/devices/lighting-devices/power",
            Json("""{"id":"dev-x","on":false}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Contains("dev-x", preset.DisabledDevices!);
    }

    // ---- mode + effect capture and restore ----

    private async Task<string> CreatePreset(string name)
    {
        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets",
            Json($$"""{"name":"{{name}}"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
    }

    private void SetLiveLook(string effect, float hue)
    {
        Store.Update(s =>
        {
            s.Lighting.Sync = effect;
            s.Lighting.Animate.Effect = effect;
            s.Lighting.Animate.States[effect] = new AnimateEffectState
            {
                Speed = 50,
                Intensity = 1f,
                Hue = hue,
                Colorize = 0f,
                Saturation = 1f,
                Contrast = 1f,
            };
        });
    }

    [Fact]
    public async Task Create_captures_the_live_mode_and_effect()
    {
        SetLiveLook("jellyfish", 0.25f);

        var id = await CreatePreset("Gaming");

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.NotNull(preset.Look);
        Assert.Equal("jellyfish", preset.Look.Sync);
        Assert.Equal("jellyfish", preset.Look.AnimateEffect);
        Assert.Equal(0.25f, preset.Look.AnimateState!.Hue);
    }

    [Fact]
    public async Task SaveCurrent_recaptures_the_live_effect()
    {
        SetLiveLook("jellyfish", 0.25f);
        var id = await CreatePreset("P");

        SetLiveLook("aurora", 0.5f);
        var res = await _client.PutAsync(
            $"/devices/lighting-devices/layout-presets/{id}",
            Json("""{"saveCurrent":true}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal("aurora", preset.Look!.AnimateEffect);
    }

    [Fact]
    public async Task Activate_restores_the_preset_effect()
    {
        SetLiveLook("jellyfish", 0.25f);
        var first = await CreatePreset("Jelly");
        SetLiveLook("aurora", 0.5f);
        await CreatePreset("Aurora");

        var res = await _client.PostAsync(
            $"/devices/lighting-devices/layout-presets/{first}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = Store.Load();
        Assert.Equal("jellyfish", stored.Lighting.Sync);
        Assert.Equal("jellyfish", stored.Lighting.Animate.Effect);
    }

    // NEX-51: two presets on one effect differing only by colour. States is
    // keyed by effect, so the preset must carry its own copy.
    [Fact]
    public async Task Activate_restores_the_colour_of_a_shared_effect()
    {
        SetLiveLook("jellyfish", 0.2f);
        var blue = await CreatePreset("Blue");
        SetLiveLook("jellyfish", 0.8f);
        var orange = await CreatePreset("Orange");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{blue}/activate", null);
        Assert.Equal(0.2f, Store.Load().Lighting.Animate.States["jellyfish"].Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{orange}/activate", null);
        Assert.Equal(0.8f, Store.Load().Lighting.Animate.States["jellyfish"].Hue);
    }

    [Fact]
    public async Task Activate_restores_the_master_brightness()
    {
        Store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        var dim = await CreatePreset("Dim");
        Store.Update(s => s.Lighting.GlobalBrightness = 1f);
        var bright = await CreatePreset("Bright");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{dim}/activate", null);
        Assert.Equal(0.3f, Store.Load().Lighting.GlobalBrightness);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{bright}/activate", null);
        Assert.Equal(1f, Store.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public async Task Setting_master_brightness_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/lighting/global-brightness", Json("""{"value":0.42}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(0.42f, preset.Look!.GlobalBrightness);
    }

    [Fact]
    public async Task Activate_restores_the_screen_filter()
    {
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.2f);
        var cool = await CreatePreset("Cool");
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.8f);
        var warm = await CreatePreset("Warm");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{cool}/activate", null);
        Assert.Equal(0.2f, Store.Load().Lighting.ScreenEffect.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{warm}/activate", null);
        Assert.Equal(0.8f, Store.Load().Lighting.ScreenEffect.Hue);
    }

    // The running Mirror effect samples the provider's holder, not the store, so
    // a preset that only persisted the filter would keep rendering the old one.
    [Fact]
    public async Task Activate_pushes_the_screen_filter_into_the_live_holder()
    {
        var provider = (Nexus.Service.Lighting.LightingProvider)
            _factory.Services.GetRequiredService<Nexus.Service.Lighting.ILightingProvider>();

        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.2f);
        var cool = await CreatePreset("Cool");
        Store.Update(s => s.Lighting.ScreenEffect.Hue = 0.8f);
        var warm = await CreatePreset("Warm");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{cool}/activate", null);
        Assert.Equal(0.2f, provider.ScreenPostProcess.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{warm}/activate", null);
        Assert.Equal(0.8f, provider.ScreenPostProcess.Hue);
    }

    [Fact]
    public async Task Activate_pushes_the_media_filter_into_the_live_holder()
    {
        var provider = (Nexus.Service.Lighting.LightingProvider)
            _factory.Services.GetRequiredService<Nexus.Service.Lighting.ILightingProvider>();

        Store.Update(s => s.Lighting.MediaEffect.Hue = 0.15f);
        var first = await CreatePreset("First");
        Store.Update(s => s.Lighting.MediaEffect.Hue = 0.75f);
        var second = await CreatePreset("Second");

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{first}/activate", null);
        Assert.Equal(0.15f, provider.MediaPostProcess.Hue);

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{second}/activate", null);
        Assert.Equal(0.75f, provider.MediaPostProcess.Hue);
    }

    [Fact]
    public async Task Setting_the_screen_filter_updates_the_active_preset()
    {
        var id = await CreatePreset("P");

        var res = await _client.PostAsync(
            "/lighting/screen/effect",
            Json("""{"hue":0.45,"colorize":0,"saturation":1,"contrast":1,"persist":true}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var preset = Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!;
        Assert.Equal(0.45f, preset.Look!.ScreenEffect!.Hue);
    }

    [Fact]
    public async Task Activate_legacy_preset_without_a_look_leaves_the_live_effect_untouched()
    {
        SetLiveLook("jellyfish", 0.25f);
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Add(new Nexus.Service.Persistence.LayoutPreset
            {
                Id = "legacy",
                Name = "Legacy",
            });
        });

        var res = await _client.PostAsync(
            "/devices/lighting-devices/layout-presets/legacy/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("jellyfish", Store.Load().Lighting.Sync);
    }
}
