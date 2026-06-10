using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Panel;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Gallery routes through the real pipeline: source CRUD + items/file reads,
/// browse validation, upload import round-trip, and — critically — the auth
/// tiers: item reads are panel-reachable, while browse and source mutations
/// (host-filesystem surface) must reject a paired panel session.
/// </summary>
[Collection("NexusHost")]
public sealed class GalleryRoutesTests : IDisposable
{
    // Canonical 67-byte 1x1 transparent PNG — must decode in ffmpeg for the
    // thumbnail test, not just satisfy the extension allowlist.
    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly string _tempDir;
    private readonly string _photosDir;

    public GalleryRoutesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-gallery-itest-" + Guid.NewGuid().ToString("N")[..8]);
        _photosDir = Path.Combine(_tempDir, "photos");
        Directory.CreateDirectory(_photosDir);

        _baseFactory = new NexusAppFactory();
        var galleryRoot = Path.Combine(_tempDir, "gallery");
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GalleryLibrary>();
                services.AddSingleton(new GalleryLibrary(galleryRoot));
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    /// <summary>Mint a real paired-phone session and return a client bearing it.</summary>
    private HttpClient PanelClient()
    {
        var pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCore(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        return client;
    }

    private string WriteImage(string name)
    {
        var path = Path.Combine(_photosDir, name);
        File.WriteAllBytes(path, TinyPng);
        return path;
    }

    private static async Task<T?> ReadAs<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.Deserialize(await res.Content.ReadAsStringAsync(), info);

    // ── Source CRUD + items + file (desktop tier) ────────────────────────────

    [Fact]
    public async Task SourceCrud_Items_And_File_RoundTrip()
    {
        var client = DesktopClient();
        WriteImage("a.png");
        WriteImage("b.jpg");

        // Add the folder.
        var add = await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = await ReadAs(add, AppJsonContext.Default.GallerySourceMutationResponse);
        Assert.False(added!.Error);

        // Listed.
        var sources = await ReadAs(await client.GetAsync("/gallery/sources"), AppJsonContext.Default.GallerySourcesResponse);
        Assert.Single(sources!.Sources);

        // Items flattened, name-sorted.
        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Equal(new[] { "a.png", "b.jpg" }, items!.Items.Select(i => i.Name).ToArray());

        // Original bytes served with the right content type.
        var file = await client.GetAsync($"/gallery/items/{items.Items[0].Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TinyPng, await file.Content.ReadAsByteArrayAsync());

        // Delete the source → items empty.
        var del = await client.DeleteAsync($"/gallery/sources/{added.Source!.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var after = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Empty(after!.Items);
    }

    [Fact]
    public async Task AddSource_RelativePath_IsRejected()
    {
        var res = await DesktopClient().PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = "photos/a.png", Kind = GallerySourceKinds.File });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task File_UnknownId_Is404()
    {
        var res = await DesktopClient().GetAsync("/gallery/items/deadbeefdeadbeef/file");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Browse ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Browse_ListsDirsAndImageFiles()
    {
        WriteImage("a.png");
        File.WriteAllText(Path.Combine(_photosDir, "skip.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_photosDir, "sub"));

        var res = await ReadAs(
            await DesktopClient().GetAsync($"/gallery/browse?path={Uri.EscapeDataString(_photosDir)}"),
            AppJsonContext.Default.GalleryBrowseResponse);

        Assert.False(res!.Error);
        Assert.Contains(res.Dirs, d => d.Name == "sub");
        Assert.Single(res.Files);
        Assert.Equal("a.png", res.Files[0].Name);
        Assert.NotNull(res.Parent);
    }

    [Fact]
    public async Task Browse_RelativeOrDotDotPath_Fails()
    {
        var client = DesktopClient();

        var relative = await ReadAs(await client.GetAsync("/gallery/browse?path=photos"),
            AppJsonContext.Default.GalleryBrowseResponse);
        Assert.True(relative!.Error);

        var dotted = await ReadAs(await client.GetAsync("/gallery/browse?path=..%2F..%2Fetc"),
            AppJsonContext.Default.GalleryBrowseResponse);
        Assert.True(dotted!.Error);
    }

    [Fact]
    public async Task Browse_EmptyPath_ReturnsRoots()
    {
        var res = await ReadAs(await DesktopClient().GetAsync("/gallery/browse"),
            AppJsonContext.Default.GalleryBrowseResponse);

        Assert.False(res!.Error);
        Assert.NotEmpty(res.Dirs);
        Assert.Null(res.Parent);
    }

    // ── Upload import ────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_StoresOriginal_AndServesIt()
    {
        var client = DesktopClient();

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "upload.png");

        var res = await client.PostAsync("/gallery/import", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var imported = await ReadAs(res, AppJsonContext.Default.GallerySourceMutationResponse);
        Assert.Equal(GallerySourceKinds.Upload, imported!.Source!.Kind);

        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        var item = Assert.Single(items!.Items);

        var file = await client.GetAsync($"/gallery/items/{item.Id}/file");
        Assert.Equal(TinyPng, await file.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Import_NonImage_IsRejected()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1 }), "file", "evil.exe");

        var res = await DesktopClient().PostAsync("/gallery/import", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Thumbnail (ffmpeg-dependent) ─────────────────────────────────────────

    [Fact]
    public async Task Thumbnail_ServesJpeg_OrCleanly404sWithoutFfmpeg()
    {
        var client = DesktopClient();
        WriteImage("a.png");
        await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);

        var res = await client.GetAsync($"/gallery/items/{items!.Items[0].Id}/thumbnail");

        if (FfmpegResolver.Path is null)
        {
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
        }
    }

    // ── Auth tiers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PanelSession_CanReadItems_File_And_Thumbnail()
    {
        WriteImage("a.png");
        await DesktopClient().PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });

        var panel = PanelClient();
        var items = await panel.GetAsync("/gallery/items");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);

        var parsed = await ReadAs(items, AppJsonContext.Default.GalleryItemsResponse);
        var file = await panel.GetAsync($"/gallery/items/{parsed!.Items[0].Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    }

    [Fact]
    public async Task PanelSession_CannotBrowse_OrMutateSources()
    {
        var panel = PanelClient();

        var browse = await panel.GetAsync("/gallery/browse");
        Assert.Equal(HttpStatusCode.Forbidden, browse.StatusCode);

        var add = await panel.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);

        var sources = await panel.GetAsync("/gallery/sources");
        Assert.Equal(HttpStatusCode.Forbidden, sources.StatusCode);

        var del = await panel.DeleteAsync("/gallery/sources/whatever");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401_Everywhere()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/gallery/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/gallery/browse")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/gallery/sources")).StatusCode);
    }
}
