using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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

/// <summary>Spy executor used by the route tests to assert test-press dispatched without constructing the real provider graph.</summary>
internal sealed class SpyDeckActionExecutor : IDeckActionExecutor
{
    public (DeckAction? Action, string Serial, int KeyIndex, string LatchKey)? LastCall;

    public Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        LastCall = (action, serial, keyIndex, latchKey);
        return Task.CompletedTask;
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey) => false;
}

/// <summary>
/// Every /streamdeck/* route is LocalhostOnly, which the auth middleware
/// gates on HttpContext.Connection.RemoteIpAddress - unset for a plain
/// WebApplicationFactory HttpClient request, so it 404s before auth even
/// runs (see AuthMiddlewareIntegrationTests.cs's doc comment). A raw
/// TestServer.SendAsync with a manually assigned request body reliably 400s
/// with an empty response for this app's minimal-API POST/PUT routes for
/// reasons that didn't resolve under investigation; this IStartupFilter
/// stamps loopback onto every request instead, which lets the rest of the
/// test use the plain HttpClient body-sending path DeckActionRoutesTests.cs
/// already proves works.
/// </summary>
internal sealed class LoopbackConnectionFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMw) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            await nextMw(ctx);
        });
        next(app);
    };
}

[Collection("NexusHost")]
public sealed class StreamDeckRoutesTests : IDisposable
{
    private readonly string _imageCacheDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-route-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SpyDeckActionExecutor _executor = new();

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
                s.RemoveAll<IDeckActionExecutor>();
                s.AddSingleton<IDeckActionExecutor>(_executor);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetConfig_WithNoPersistedDeck_ReturnsAnEmptyConfig()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/decks/UNKNOWN-SERIAL/config");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Empty(doc.RootElement.GetProperty("config").GetProperty("slots").EnumerateArray());
        }
    }

    [Fact]
    public async Task PutConfig_ThenGetConfig_RoundTripsTheSlotTree()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putBody = "{\"config\":{\"slots\":[{\"label\":\"Lock\",\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}}";
            var put = await client.PutAsync("/streamdeck/decks/SERIAL-1/config", Json(putBody));
            Assert.True(put.IsSuccessStatusCode);

            var get = await client.GetAsync("/streamdeck/decks/SERIAL-1/config");
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var slots = doc.RootElement.GetProperty("config").GetProperty("slots");
            Assert.Equal(1, slots.GetArrayLength());
            Assert.Equal("Lock", slots[0].GetProperty("label").GetString());
            Assert.Equal("lock", slots[0].GetProperty("action").GetProperty("action").GetString());

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal("Lock", store.Load().StreamDeck.Decks["SERIAL-1"].Deck.Slots[0].Label);
        }
    }

    [Fact]
    public async Task UpdateDeck_PersistsNameAndBrightness()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1", Json("{\"name\":\"Desk Deck\",\"brightness\":77}"));
            Assert.True(res.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal("Desk Deck", deck.Name);
            Assert.Equal(77, deck.Brightness);
        }
    }

    [Fact]
    public async Task UploadImage_CachesToDiskAndReturnsTheHash()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 1, 2, 3, 4, 5 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", content);
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var hash = doc.RootElement.GetProperty("hash").GetString();
            Assert.False(string.IsNullOrEmpty(hash));
            Assert.Equal(StreamDeckImageCache.Hash(bytes), hash);

            var onDisk = Path.Combine(_imageCacheDir, "SERIAL-1", hash + ".bin");
            Assert.True(File.Exists(onDisk));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(onDisk));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(hash, store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["0/0"]);
        }
    }

    [Fact]
    public async Task UploadImage_BackSlotPath_CachesUnderTheReservedKey()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 9, 9, 9 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/back/0", content);
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var hash = doc.RootElement.GetProperty("hash").GetString();
            Assert.False(string.IsNullOrEmpty(hash));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(hash, store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["back/0"]);
        }
    }

    [Fact]
    public async Task TestPress_DispatchesTheResolvedSlotActionToTheExecutor()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
            {
                Deck = new DeckConfig { Slots = { new DeckSlot { Action = new DeckAction { Type = "power", PowerAction = "lock" } } } },
            });

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            Assert.NotNull(_executor.LastCall);
            Assert.Equal("SERIAL-1", _executor.LastCall!.Value.Serial);
            Assert.Equal("lock", _executor.LastCall.Value.Action!.PowerAction);
        }
    }

    [Fact]
    public async Task TestPress_UnknownDeck_Fails()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/NEVER-SEEN/test-press/0", Json("{}"));
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
            Assert.Null(_executor.LastCall);
        }
    }

    [Fact]
    public async Task GetDecks_FromLan_404s()
    {
        // Deliberately does NOT use Boot()'s loopback-forcing filter - this
        // is the one test that needs a real non-loopback RemoteIpAddress, so
        // it uses the plain factory + Server.SendAsync (a bodyless GET, which
        // AuthMiddlewareIntegrationTests.cs already proves works with SendAsync).
        var factory = new NexusAppFactory();
        var token = factory.Services.GetRequiredService<TokenService>().Token;
        var ctx = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/streamdeck/decks";
            c.Request.Headers.Authorization = "Bearer " + token;
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task GetDecks_OnLoopbackWithoutToken_401s()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = factory.CreateClient();
            var res = await anon.GetAsync("/streamdeck/decks");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }
}
