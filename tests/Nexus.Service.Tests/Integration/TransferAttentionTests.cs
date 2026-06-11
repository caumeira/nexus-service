using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Transfer;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Transfer;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The native-notification gate: <see cref="TransferInbox.TransferNeedsAttention"/>
/// fires only when no dashboard WebSocket is subscribed to the "transfer" topic,
/// and at most once per request (a batch must not pop one balloon per file).
/// </summary>
[Collection("NexusHost")]
public sealed class TransferAttentionTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly string _tempDir;
    private readonly string _inboxDir;
    private readonly List<TransferAttentionNotice> _notices = new();

    public TransferAttentionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-attention-itest-" + Guid.NewGuid().ToString("N")[..8]);
        _inboxDir = Path.Combine(_tempDir, "inbox");
        Directory.CreateDirectory(_tempDir);

        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClipboardProvider>();
                services.AddSingleton<IClipboardProvider>(new AlwaysOkClipboard());
            }));

        _factory.Services.GetRequiredService<IConfigStore>()
            .Update(s => s.TransferInboxPath = _inboxDir);
        _factory.Services.GetRequiredService<TransferInbox>()
            .TransferNeedsAttention += n => { lock (_notices) _notices.Add(n); };
    }

    private sealed class AlwaysOkClipboard : IClipboardProvider
    {
        public bool SetText(string text) => true;
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

    private static MultipartFormDataContent FilePayload(params string[] names)
    {
        var content = new MultipartFormDataContent();
        foreach (var name in names)
        {
            var part = new ByteArrayContent(new byte[] { 1 });
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", name);
        }
        return content;
    }

    [Fact]
    public async Task Items_with_no_dashboard_raise_one_summary_notice()
    {
        var res = await TestPhoneSession.CreateClient(_factory)
            .PostAsync("/transfer/items", FilePayload("a.bin", "b.bin"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        TransferAttentionNotice notice;
        lock (_notices)
        {
            notice = Assert.Single(_notices);
        }
        Assert.Equal(_inboxDir, notice.FolderPath);
        Assert.Contains("2 files", notice.Text);
        Assert.Contains("Test Phone", notice.Text);
    }

    [Fact]
    public async Task Clipboard_with_no_dashboard_raises_notice_without_folder()
    {
        var res = await TestPhoneSession.CreateClient(_factory)
            .PostAsJsonAsync("/transfer/clipboard", new TransferClipboardBody { Text = "hi" },
                AppJsonContext.Default.TransferClipboardBody);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        TransferAttentionNotice notice;
        lock (_notices)
        {
            notice = Assert.Single(_notices);
        }
        Assert.Null(notice.FolderPath);
        Assert.Contains("Clipboard", notice.Text);
    }

    [Fact]
    public async Task Items_with_dashboard_subscribed_raise_nothing()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = "Bearer " + token;
        using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"sub\":[\"{PanelTopics.Transfer}\"]}}"),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        // Subscription lands asynchronously on the server's receive loop; wait
        // on the hub's own state, not a fixed sleep.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!hub.TopicHasSubscribers(PanelTopics.Transfer))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }

        var res = await TestPhoneSession.CreateClient(_factory)
            .PostAsync("/transfer/items", FilePayload("seen.bin"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        lock (_notices)
        {
            Assert.Empty(_notices);
        }
    }
}
