using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Deck "Presets" routes (plan deck-monitoring-and-presets.md Task 2). Reuses
/// the LoopbackConnectionFilter defined in StreamDeckRoutesTests.cs - every
/// /streamdeck/* route is LocalhostOnly, which needs a loopback
/// RemoteIpAddress to pass auth in a WebApplicationFactory test client.
/// </summary>
[Collection("NexusHost")]
public sealed class StreamDeckPresetRoutesTests : IDisposable
{
    private readonly string _imageCacheDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-preset-cache-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_imageCacheDir, recursive: true); } catch { /* best effort */ }
    }

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddTransient<IStartupFilter, LoopbackConnectionFilter>();
                s.RemoveAll<StreamDeckImageCache>();
                s.AddSingleton(new StreamDeckImageCache(_imageCacheDir));
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static ByteArrayContent ImageBytes(params byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    // ---- GET ----

    [Fact]
    public async Task Get_returns_empty_list_and_null_activeId_for_an_unknown_serial()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/decks/UNKNOWN-SERIAL/presets");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("presets").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("activeId").ValueKind);
        }
    }

    // ---- POST create ----

    [Fact]
    public async Task Create_appends_preset_deep_copies_config_and_image_refs_and_sets_activeId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putConfig = await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Lock","action":{"type":"power","action":"lock"}}]}]}}"""));
            Assert.True(putConfig.IsSuccessStatusCode);
            var upload = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(1, 2, 3));
            Assert.True(upload.IsSuccessStatusCode);

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Gaming"}"""));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var preset = doc.RootElement.GetProperty("preset");
            var id = preset.GetProperty("id").GetString();
            Assert.Equal("Gaming", preset.GetProperty("name").GetString());
            Assert.False(string.IsNullOrEmpty(id));
            Assert.Equal(id, doc.RootElement.GetProperty("activeId").GetString());

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Single(deck.Presets, p => p.Id == id);
            Assert.Equal(id, deck.ActivePresetId);
            var saved = deck.Presets[0];
            Assert.Equal("Lock", saved.Deck.Pages[0].Slots[0].Label);
            Assert.Equal(deck.ImageRefs["0/0"], saved.ImageRefs["0/0"]);
        }
    }

    [Fact]
    public async Task Create_snapshot_is_independent_of_later_live_config_changes()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Original"}]}]}}"""));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Changed"}]}]}}"""));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var preset = store.Load().StreamDeck.Decks["SERIAL-1"].Presets.Single(p => p.Id == id);
            Assert.Equal("Original", preset.Deck.Pages[0].Slots[0].Label);
        }
    }

    [Fact]
    public async Task Create_cap_returns_400_when_at_10_presets()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            for (var i = 0; i < 10; i++)
            {
                var r = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("{\"name\":\"Preset " + i + "\"}"));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Overflow"}"""));

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(10, store.Load().StreamDeck.Decks["SERIAL-1"].Presets.Count);
        }
    }

    // ---- PUT update ----

    [Fact]
    public async Task Put_saveCurrent_overwrites_preset_config_and_image_refs()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"A"}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(1, 1, 1));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Original"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"B"}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(2, 2, 2));

            var putRes = await client.PutAsync(
                $"/streamdeck/decks/SERIAL-1/presets/{id}",
                Json("""{"saveCurrent":true}"""));
            Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            var preset = deck.Presets.Single(p => p.Id == id);
            Assert.Equal("B", preset.Deck.Pages[0].Slots[0].Label);
            Assert.Equal(deck.ImageRefs["0/0"], preset.ImageRefs["0/0"]);
        }
    }

    [Fact]
    public async Task Put_name_renames_preset_only()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Old Name"}"""));
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var id = store.Load().StreamDeck.Decks["SERIAL-1"].Presets[0].Id;

            var putRes = await client.PutAsync(
                $"/streamdeck/decks/SERIAL-1/presets/{id}",
                Json("""{"name":"New Name","saveCurrent":false}"""));
            Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

            Assert.Equal("New Name", store.Load().StreamDeck.Decks["SERIAL-1"].Presets[0].Name);
        }
    }

    [Fact]
    public async Task Put_returns_404_for_unknown_id()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));

            var res = await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/presets/nonexistent",
                Json("""{"name":"X"}"""));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Put_returns_404_for_unknown_serial()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PutAsync(
                "/streamdeck/decks/NEVER-SEEN/presets/some-id",
                Json("""{"name":"X"}"""));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_active_preset_nulls_activeId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P1"}"""));
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var id = store.Load().StreamDeck.Decks["SERIAL-1"].Presets[0].Id;

            var res = await client.DeleteAsync($"/streamdeck/decks/SERIAL-1/presets/{id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activeId").ValueKind);

            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Empty(deck.Presets);
            Assert.Null(deck.ActivePresetId);
        }
    }

    [Fact]
    public async Task Delete_non_active_preset_leaves_activeId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P1"}"""));
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P2"}"""));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var presets = store.Load().StreamDeck.Decks["SERIAL-1"].Presets;
            var firstId = presets[0].Id;
            var secondId = presets[1].Id;

            await client.PutAsync("/streamdeck/decks/SERIAL-1/presets/active", Json("{\"id\":\"" + secondId + "\"}"));
            var res = await client.DeleteAsync($"/streamdeck/decks/SERIAL-1/presets/{firstId}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(secondId, doc.RootElement.GetProperty("activeId").GetString());

            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Single(deck.Presets);
            Assert.Equal(secondId, deck.ActivePresetId);
        }
    }

    [Fact]
    public async Task Delete_unknown_id_is_a_noop_returning_current_activeId()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.DeleteAsync("/streamdeck/decks/SERIAL-1/presets/nonexistent");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("activeId").ValueKind);
        }
    }

    // ---- PUT /active ----

    [Fact]
    public async Task Put_active_sets_id_without_changing_live_config()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Live"}]}]}}"""));
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var id = store.Load().StreamDeck.Decks["SERIAL-1"].Presets[0].Id;

            store.Update(s => s.StreamDeck.Decks["SERIAL-1"].ActivePresetId = null);

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/presets/active", Json("{\"id\":\"" + id + "\"}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal(id, deck.ActivePresetId);
            Assert.Equal("Live", deck.Deck.Pages[0].Slots[0].Label);
        }
    }

    // ---- POST /{id}/activate ----

    [Fact]
    public async Task Activate_copies_preset_config_and_image_refs_onto_the_live_deck()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Saved"}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(9, 9, 9));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Saved"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var savedHash = store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["0/0"];

            // Change the live config + image after saving the preset.
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Changed"}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(1, 1, 1));

            var res = await client.PostAsync($"/streamdeck/decks/SERIAL-1/presets/{id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal(id, deck.ActivePresetId);
            Assert.Equal("Saved", deck.Deck.Pages[0].Slots[0].Label);
            Assert.Equal(savedHash, deck.ImageRefs["0/0"]);
        }
    }

    [Fact]
    public async Task Activate_copies_a_v2_page_qualified_image_ref_onto_the_live_deck()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"P0"}]},{"slots":[{"label":"P1"}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/1.0/0", ImageBytes(4, 5, 6));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var savedHash = store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["1.0/0"];

            // A live-only overwrite of the same v2 key after the save.
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/1.0/0", ImageBytes(7, 8, 9));

            var res = await client.PostAsync($"/streamdeck/decks/SERIAL-1/presets/{id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal(savedHash, deck.ImageRefs["1.0/0"]);
        }
    }

    [Fact]
    public async Task Activate_returns_404_for_unknown_id()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets/nonexistent/activate", null);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Activate_returns_404_for_unknown_serial()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/NEVER-SEEN/presets/some-id/activate", null);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Activate_clamps_current_page_and_clears_an_invalid_folder_path()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;

            // Preset: a single empty page (no folder at slot 0).
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json("""{"config":{"pages":[{"slots":[]}]}}"""));
            var createRes = await client.PostAsync($"/streamdeck/decks/{serial}/presets", Json("""{"name":"Small"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // Live config grows to 3 pages, each with a folder at slot 0; navigate deep into it.
            var threePages = """{"config":{"pages":[{"slots":[{"folder":{"slots":[{}]}}]},{"slots":[{"folder":{"slots":[{}]}}]},{"slots":[{"folder":{"slots":[{}]}}]}]}}""";
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json(threePages));
            var navRes = await client.PostAsync($"/streamdeck/decks/{serial}/nav", Json("""{"page":2,"folderPath":[0]}"""));
            Assert.True(navRes.IsSuccessStatusCode);
            Assert.Equal(2, worker.GetCurrentPage(serial));
            Assert.Equal(new[] { 0 }, worker.GetFolderPath(serial));

            var res = await client.PostAsync($"/streamdeck/decks/{serial}/presets/{id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            Assert.Equal(0, worker.GetCurrentPage(serial));
            Assert.Empty(worker.GetFolderPath(serial));
        }
    }

    [Fact]
    public async Task Activate_opensPageOne_evenWhenThePresetAlsoHasThatPage()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;

            // A three-page preset - so page 2 stays valid after activation and
            // the reset is a genuine reset, not the clamp the case above covers.
            var threePages = """{"config":{"pages":[{"slots":[]},{"slots":[]},{"slots":[]}]}}""";
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json(threePages));
            var createRes = await client.PostAsync($"/streamdeck/decks/{serial}/presets", Json("""{"name":"Three"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            var navRes = await client.PostAsync($"/streamdeck/decks/{serial}/nav", Json("""{"page":2,"folderPath":[]}"""));
            Assert.True(navRes.IsSuccessStatusCode);
            Assert.Equal(2, worker.GetCurrentPage(serial));

            var res = await client.PostAsync($"/streamdeck/decks/{serial}/presets/{id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(0, worker.GetCurrentPage(serial));
        }
    }

    // ---- eviction respects presets ----

    [Fact]
    public async Task UploadImage_ReplacingASlot_KeepsTheHashIfStillHeldByAPreset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var original = new byte[] { 5, 5, 5 };
            var originalHash = StreamDeckImageCache.Hash(original);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(original));
            await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Holder"}"""));

            var replacement = new byte[] { 6, 6, 6 };
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(replacement));

            // The preset still references originalHash even though the live
            // slot moved on, so the blob must survive the eviction check.
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", originalHash + ".bin")));
        }
    }

    [Fact]
    public async Task Delete_evicts_a_blob_orphaned_by_the_removed_preset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 7, 7, 7 };
            var hash = StreamDeckImageCache.Hash(bytes);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(bytes));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Only"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // Move the live slot off the hash, then re-point it elsewhere so only
            // the preset (not any live ref) still holds it before the delete.
            var other = new byte[] { 8, 8, 8 };
            var otherHash = StreamDeckImageCache.Hash(other);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(other));
            var blobPath = Path.Combine(_imageCacheDir, "SERIAL-1", hash + ".bin");
            Assert.True(File.Exists(blobPath), "the preset should have kept the original blob alive");

            var res = await client.DeleteAsync($"/streamdeck/decks/SERIAL-1/presets/{id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            Assert.False(File.Exists(blobPath));
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", otherHash + ".bin")));
        }
    }

    [Fact]
    public async Task Delete_keeps_a_blob_still_referenced_by_the_live_deck()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var shared = new byte[] { 3, 1, 4 };
            var sharedHash = StreamDeckImageCache.Hash(shared);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0.0/0", ImageBytes(shared));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // Live slot 0.0/0 still points at sharedHash - deleting the preset must not evict it.
            var res = await client.DeleteAsync($"/streamdeck/decks/SERIAL-1/presets/{id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", sharedHash + ".bin")));
        }
    }

    [Fact]
    public async Task Delete_keeps_a_blob_still_referenced_by_another_preset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var shared = new byte[] { 2, 4, 6 };
            var sharedHash = StreamDeckImageCache.Hash(shared);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(shared));
            var firstCreate = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"First"}"""));
            using var firstDoc = JsonDocument.Parse(await firstCreate.Content.ReadAsStringAsync());
            var firstId = firstDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
            var secondCreate = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Second"}"""));
            using var secondDoc = JsonDocument.Parse(await secondCreate.Content.ReadAsStringAsync());
            var secondId = secondDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // Clear the live ref so only the two presets hold sharedHash.
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(9, 9, 9));

            var res = await client.DeleteAsync($"/streamdeck/decks/SERIAL-1/presets/{firstId}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", sharedHash + ".bin")));
            Assert.NotEmpty(secondId);
        }
    }

    [Fact]
    public async Task SaveCurrent_evicts_a_blob_orphaned_by_the_overwrite()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var original = new byte[] { 1, 3, 5 };
            var originalHash = StreamDeckImageCache.Hash(original);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(original));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // Replace the live image (the preset keeps originalHash alive here).
            var replacement = new byte[] { 2, 4, 6 };
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(replacement));
            var blobPath = Path.Combine(_imageCacheDir, "SERIAL-1", originalHash + ".bin");
            Assert.True(File.Exists(blobPath));

            // saveCurrent re-snapshots the preset onto the replacement image,
            // dropping the last reference to originalHash.
            var putRes = await client.PutAsync($"/streamdeck/decks/SERIAL-1/presets/{id}", Json("""{"saveCurrent":true}"""));
            Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

            Assert.False(File.Exists(blobPath));
        }
    }

    [Fact]
    public async Task Activate_evicts_a_live_blob_orphaned_by_the_swap()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            // Save a preset while the live slot still points at savedBytes.
            var saved = new byte[] { 1, 1, 1 };
            var savedHash = StreamDeckImageCache.Hash(saved);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(saved));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"P"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            // A live-only upload after the save is not captured by any preset.
            var liveOnly = new byte[] { 2, 2, 2 };
            var liveOnlyHash = StreamDeckImageCache.Hash(liveOnly);
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(liveOnly));
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", liveOnlyHash + ".bin")));

            var res = await client.PostAsync($"/streamdeck/decks/SERIAL-1/presets/{id}/activate", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            // Activate swaps ImageRefs back to the preset's savedHash - nothing
            // references liveOnlyHash anymore, so it must be evicted.
            Assert.False(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", liveOnlyHash + ".bin")));
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", savedHash + ".bin")));
        }
    }

    // ---- serialization round trip ----

    [Fact]
    public async Task Preset_round_trips_through_settings_persistence()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PutAsync(
                "/streamdeck/decks/SERIAL-1/config",
                Json("""{"config":{"pages":[{"slots":[{"label":"Lock","action":{"type":"power","action":"lock"}}]}]}}"""));
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", ImageBytes(1, 2, 3));
            var createRes = await client.PostAsync("/streamdeck/decks/SERIAL-1/presets", Json("""{"name":"Roundtrip"}"""));
            using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;

            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.FlushNow();

            using var raw = new JsonConfigStore(store.SettingsPath);
            var reloaded = raw.Load();
            var deck = reloaded.StreamDeck.Decks["SERIAL-1"];
            Assert.Equal(id, deck.ActivePresetId);
            var preset = deck.Presets.Single(p => p.Id == id);
            Assert.Equal("Roundtrip", preset.Name);
            Assert.Equal("Lock", preset.Deck.Pages[0].Slots[0].Label);
            Assert.Equal("lock", preset.Deck.Pages[0].Slots[0].Action!.PowerAction);
            Assert.Equal(deck.ImageRefs["0/0"], preset.ImageRefs["0/0"]);
        }
    }
}
