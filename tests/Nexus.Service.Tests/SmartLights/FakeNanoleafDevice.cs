using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// In-process Nanoleaf controller emulator: a loopback HTTP server playing the
/// Open API (pairing, info, state, effects, identify) plus a UDP listener for
/// External Control v2 datagrams. Configure <see cref="Panels"/> for a panel
/// product or <see cref="NumLeds"/> for an Essentials strip. Records every
/// write for assertions.
/// </summary>
internal sealed class FakeNanoleafDevice : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly Socket _udp;
    private readonly CancellationTokenSource _cts = new();

    public FakeNanoleafDevice()
    {
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        UdpPort = ((IPEndPoint)_udp.LocalEndPoint!).Port;

        HttpPort = BindHttpOnFreePort(_http);
        _ = Task.Run(() => HttpLoopAsync(_cts.Token));
        _ = Task.Run(() => UdpLoopAsync(_cts.Token));
    }

    public int HttpPort { get; }
    public int UdpPort { get; }

    public string Token { get; } = "TESTTOKEN1234567890";
    public volatile bool PairingWindowOpen = true;
    public string SerialNo = "S16331A0217";
    public string Name = "Office Shapes";
    public string Model = "NL42";
    /// <summary>(panelId, x, y, shapeType) — include non-light parts to test
    /// filtering. Empty + <see cref="NumLeds"/> set = Essentials device.</summary>
    public List<(int Id, int X, int Y, int Shape)> Panels = new();
    public int? NumLeds;

    public ConcurrentQueue<string> StateBodies { get; } = new();
    public ConcurrentQueue<string> EffectsBodies { get; } = new();
    public ConcurrentQueue<byte[]> UdpPackets { get; } = new();
    public int IdentifyCount;

    private static int BindHttpOnFreePort(HttpListener http)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                http.Prefixes.Clear();
                http.Prefixes.Add($"http://127.0.0.1:{port}/");
                http.Start();
                return port;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Port raced away between probe and bind — try another.
            }
        }
    }

    private async Task HttpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            try { Handle(ctx); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
        var method = ctx.Request.HttpMethod;
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        if (method == "POST" && path == "/api/v1/new")
        {
            if (!PairingWindowOpen) { Respond(ctx, 403, null); return; }
            Respond(ctx, 200, $$"""{"auth_token":"{{Token}}"}""");
            return;
        }

        var prefix = $"/api/v1/{Token}";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { Respond(ctx, 401, null); return; }
        var rest = path.Substring(prefix.Length);

        switch (method, rest)
        {
            case ("GET", ""):
                Respond(ctx, 200, BuildInfoJson());
                break;
            case ("GET", "/length"):
                if (NumLeds is { } n) Respond(ctx, 200, $$"""{"numLEDs":{{n}}}""");
                else Respond(ctx, 404, null);
                break;
            case ("PUT", "/state"):
                StateBodies.Enqueue(body);
                Respond(ctx, 204, null);
                break;
            case ("PUT", "/effects"):
                EffectsBodies.Enqueue(body);
                Respond(ctx, 204, null);
                break;
            case ("PUT", "/identify"):
                Interlocked.Increment(ref IdentifyCount);
                Respond(ctx, 204, null);
                break;
            case ("GET", "/state/on"):
                Respond(ctx, 200, """{"value":true,"max":1,"min":0}""");
                break;
            default:
                Respond(ctx, 404, null);
                break;
        }
    }

    private string BuildInfoJson()
    {
        var sb = new StringBuilder();
        sb.Append($$"""{"name":"{{Name}}","serialNo":"{{SerialNo}}","manufacturer":"Nanoleaf","firmwareVersion":"8.5.2","model":"{{Model}}" """ .TrimEnd());
        if (Panels.Count > 0)
        {
            sb.Append($$""","panelLayout":{"layout":{"numPanels":{{Panels.Count}},"sideLength":134,"positionData":[""");
            for (var i = 0; i < Panels.Count; i++)
            {
                var (id, x, y, shape) = Panels[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"panelId\":").Append(id)
                  .Append(",\"x\":").Append(x)
                  .Append(",\"y\":").Append(y)
                  .Append(",\"o\":0,\"shapeType\":").Append(shape)
                  .Append('}');
            }
            sb.Append("]},\"globalOrientation\":{\"value\":0,\"max\":360,\"min\":0}}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void Respond(HttpListenerContext ctx, int status, string? json)
    {
        ctx.Response.StatusCode = status;
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        ctx.Response.Close();
    }

    private async Task UdpLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await _udp.ReceiveFromAsync(buf, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (res.ReceivedBytes > 0) UdpPackets.Enqueue(buf.AsSpan(0, res.ReceivedBytes).ToArray());
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try { _http.Stop(); } catch { }
        try { _http.Close(); } catch { }
        _udp.Dispose();
    }
}
