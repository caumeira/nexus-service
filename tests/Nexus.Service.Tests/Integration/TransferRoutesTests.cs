using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Transfer;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Serialization;
using Nexus.Service.Transfer;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Phone→PC transfer routes through the real pipeline: multipart uploads land
/// in the configured inbox with sanitized/uniquified names, clipboard text
/// reaches the (stubbed) clipboard provider, and the auth tiers hold - both
/// endpoints are panel-reachable for a paired phone session and locked for
/// anonymous callers.
/// </summary>
[Collection("NexusHost")]
public sealed class TransferRoutesTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly string _tempDir;
    private readonly string _inboxDir;
    private readonly RecordingClipboard _clipboard = new();

    public TransferRoutesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-transfer-itest-" + Guid.NewGuid().ToString("N")[..8]);
        _inboxDir = Path.Combine(_tempDir, "inbox");
        Directory.CreateDirectory(_tempDir);

        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // The real provider mutates the developer's OS clipboard.
                services.RemoveAll<IClipboardProvider>();
                services.AddSingleton<IClipboardProvider>(_clipboard);
            }));

        _factory.Services.GetRequiredService<IConfigStore>()
            .Update(s => s.TransferInboxPath = _inboxDir);
    }

    private sealed class RecordingClipboard : IClipboardProvider
    {
        public string? LastText;
        public bool Available = true;

        public bool SetText(string text)
        {
            if (!Available)
                return false;
            LastText = text;
            return true;
        }
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

    private HttpClient PanelClient() => TestPhoneSession.CreateClient(_factory);

    private static MultipartFormDataContent FilePayload(params (string Name, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", name);
        }
        return content;
    }

    // ── Auth tiers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Items_without_token_returns_401()
    {
        var res = await _factory.CreateClient()
            .PostAsync("/transfer/items", FilePayload(("a.txt", new byte[] { 1 })));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Clipboard_without_token_returns_401()
    {
        var res = await _factory.CreateClient()
            .PostAsJsonAsync("/transfer/clipboard", new TransferClipboardBody { Text = "hi" },
                AppJsonContext.Default.TransferClipboardBody);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── File uploads ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Items_from_phone_session_land_in_inbox()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var res = await PanelClient().PostAsync("/transfer/items", FilePayload(("IMG_0001.png", payload)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.TransferItemsResponse);
        Assert.NotNull(body);
        Assert.Single(body!.Saved);
        Assert.Equal("IMG_0001.png", body.Saved[0].Name);
        Assert.Equal(_inboxDir, body.Inbox);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_inboxDir, "IMG_0001.png")));
        // No staging leftovers next to the final file.
        Assert.Empty(Directory.GetFiles(_inboxDir, "*.nexus-partial"));
    }

    [Fact]
    public async Task Items_with_traversal_name_are_sanitized_into_inbox()
    {
        var res = await PanelClient().PostAsync("/transfer/items",
            FilePayload(("../../evil.txt", new byte[] { 1, 2 })));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.TransferItemsResponse);
        Assert.Equal("evil.txt", body!.Saved[0].Name);
        Assert.True(File.Exists(Path.Combine(_inboxDir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.txt")));
    }

    [Fact]
    public async Task Items_with_colliding_names_are_uniquified()
    {
        var client = PanelClient();
        await client.PostAsync("/transfer/items", FilePayload(("dup.txt", new byte[] { 1 })));
        var res = await client.PostAsync("/transfer/items", FilePayload(("dup.txt", new byte[] { 2 })));

        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.TransferItemsResponse);
        Assert.Equal("dup (2).txt", body!.Saved[0].Name);
        Assert.True(File.Exists(Path.Combine(_inboxDir, "dup.txt")));
        Assert.True(File.Exists(Path.Combine(_inboxDir, "dup (2).txt")));
    }

    [Fact]
    public async Task Items_with_multiple_files_saves_all()
    {
        var res = await PanelClient().PostAsync("/transfer/items",
            FilePayload(("one.bin", new byte[] { 1 }), ("two.bin", new byte[] { 2, 2 })));

        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.TransferItemsResponse);
        Assert.Equal(2, body!.Saved.Count);
        Assert.True(File.Exists(Path.Combine(_inboxDir, "one.bin")));
        Assert.True(File.Exists(Path.Combine(_inboxDir, "two.bin")));
    }

    [Fact]
    public async Task Items_with_concurrent_same_names_all_save()
    {
        var client = PanelClient();
        var posts = Enumerable.Range(0, 8)
            .Select(i => client.PostAsync("/transfer/items", FilePayload(("race.bin", new byte[] { (byte)i }))))
            .ToArray();
        var results = await Task.WhenAll(posts);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var files = Directory.GetFiles(_inboxDir, "race*")
            .Where(f => !f.EndsWith(".nexus-partial", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(8, files.Length);
    }

    [Fact]
    public async Task Items_without_files_returns_400()
    {
        var res = await PanelClient().PostAsync("/transfer/items", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Items_with_non_form_body_returns_400()
    {
        var res = await PanelClient().PostAsync("/transfer/items",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clipboard_from_phone_session_sets_provider_text()
    {
        var res = await PanelClient().PostAsJsonAsync("/transfer/clipboard",
            new TransferClipboardBody { Text = "hello from phone" },
            AppJsonContext.Default.TransferClipboardBody);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ApiResponse);
        Assert.False(body!.Error);
        Assert.Equal("hello from phone", _clipboard.LastText);
    }

    [Fact]
    public async Task Clipboard_with_empty_text_fails()
    {
        var res = await PanelClient().PostAsJsonAsync("/transfer/clipboard",
            new TransferClipboardBody { Text = "" },
            AppJsonContext.Default.TransferClipboardBody);

        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ApiResponse);
        Assert.True(body!.Error);
    }

    [Fact]
    public async Task Clipboard_when_provider_unavailable_fails()
    {
        _clipboard.Available = false;
        try
        {
            var res = await PanelClient().PostAsJsonAsync("/transfer/clipboard",
                new TransferClipboardBody { Text = "x" },
                AppJsonContext.Default.TransferClipboardBody);

            var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ApiResponse);
            Assert.True(body!.Error);
        }
        finally
        {
            _clipboard.Available = true;
        }
    }

    [Fact]
    public async Task Clipboard_over_cap_fails()
    {
        var res = await PanelClient().PostAsJsonAsync("/transfer/clipboard",
            new TransferClipboardBody { Text = new string('x', TransferInbox.MaxClipboardChars + 1) },
            AppJsonContext.Default.TransferClipboardBody);

        var body = await res.Content.ReadFromJsonAsync(AppJsonContext.Default.ApiResponse);
        Assert.True(body!.Error);
        Assert.NotEqual(new string('x', TransferInbox.MaxClipboardChars + 1), _clipboard.LastText);
    }
}

/// <summary>Pure sanitization rules - no host needed.</summary>
public sealed class TransferInboxNameTests
{
    // Only cross-platform invariants here: control chars and '/' are replaced
    // everywhere; Windows-only invalid chars (':', '<', …) follow the host OS.
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("a b (1).jpg", "a b (1).jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData("", "transfer")]
    [InlineData("   ", "transfer")]
    [InlineData(".hidden", "hidden")]
    [InlineData("name.", "name")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("CON.foo.txt", "_CON.foo.txt")]
    [InlineData("a\tb.txt", "a_b.txt")]
    public void Sanitizes(string raw, string expected)
        => Assert.Equal(expected, TransferInbox.SanitizeFileName(raw));

    [Fact]
    public void Caps_stem_length_and_keeps_extension()
    {
        var sanitized = TransferInbox.SanitizeFileName(new string('a', 300) + ".mov");
        Assert.Equal(new string('a', 120) + ".mov", sanitized);
    }

    [Fact]
    public void Caps_extension_length()
    {
        var sanitized = TransferInbox.SanitizeFileName("a." + new string('b', 300));
        Assert.Equal("a." + new string('b', 23), sanitized);
    }
}
