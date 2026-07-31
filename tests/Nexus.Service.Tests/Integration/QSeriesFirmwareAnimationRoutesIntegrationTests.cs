using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET/PUT /devices/qseries/firmware-animation over the real request pipeline,
/// with the <see cref="QSeriesCoolerHub"/> singleton swapped for one wired to a
/// fake discovery + transport at the DI seam (mirrors CoolingRoutesIntegrationTests).
/// </summary>
[Collection("NexusHost")]
public sealed class QSeriesFirmwareAnimationRoutesIntegrationTests
{
    private (WebApplicationFactory<Program> factory, HttpClient client) Boot(QSeriesCoolerHub hub)
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<QSeriesCoolerHub>();
                s.AddSingleton(hub);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static QSeriesCoolerHub NewDisconnectedHub() =>
        new(new FakeDiscovery(), _ => new StatefulTransport());

    private static QSeriesCoolerHub NewConnectedHub()
    {
        var hub = new QSeriesCoolerHub(
            new FakeDiscovery(new QSeriesCoolerPort { PortName = "COM_TEST", Serial = "QINT123", Variant = QSeriesCoolerProtocol.VariantQ60 }),
            _ => new StatefulTransport());
        hub.EnsureConnected();
        // Above the Q60 FwAnimation/FwAnimationBrightness thresholds so the route's
        // SupportsFirmwareAnimation gate does not itself block these tests.
        hub.State.FirmwareVersion = "2.0.9.1";
        return hub;
    }

    [Fact]
    public async Task Get_WhenNotConnected_Returns409()
    {
        var (factory, client) = Boot(NewDisconnectedHub());
        using (factory)
        {
            var res = await client.GetAsync("/devices/qseries/firmware-animation");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            // Error bodies must be a source-gen-registered type: an anonymous body
            // serializes under JIT reflection but throws 500 on the AOT binary.
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("msg").GetString()));
        }
    }

    [Fact]
    public async Task Put_WhenNotConnected_Returns409()
    {
        var (factory, client) = Boot(NewDisconnectedHub());
        using (factory)
        {
            var res = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation = 1, r = 1, g = 2, b = 3, brightness = 50 });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    [Fact]
    public async Task Put_WhenFirmwarePredatesSupport_Returns409()
    {
        var hub = NewConnectedHub();
        hub.State.FirmwareVersion = "1.9.9.9"; // below the Q60 FwAnimation threshold
        var (factory, client) = Boot(hub);
        using (factory)
        {
            var res = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation = QSeriesCoolerProtocol.FwAnimationColor, r = 1, g = 2, b = 3, brightness = 50 });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    [Fact]
    public async Task GetState_ReportsSupportFlagsFromFirmwareVersion()
    {
        var hub = NewConnectedHub();
        hub.State.FirmwareVersion = "2.0.1.1"; // supports FwAnimation, not yet FwAnimationBrightness
        var (factory, client) = Boot(hub);
        using (factory)
        {
            var res = await client.GetAsync("/devices/qseries");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("fwAnimationSupported").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("fwAnimationBrightnessSupported").GetBoolean());
        }
    }

    [Fact]
    public async Task Put_WithValidBody_PersistsAndGetReflectsIt()
    {
        var (factory, client) = Boot(NewConnectedHub());
        using (factory)
        {
            var put = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation = QSeriesCoolerProtocol.FwAnimationRainbowGradient, r = 200, g = 100, b = 50, brightness = 90 });
            Assert.True(put.IsSuccessStatusCode);

            var res = await client.GetAsync("/devices/qseries/firmware-animation");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.True(root.GetProperty("supported").GetBoolean());
            Assert.Equal(QSeriesCoolerProtocol.FwAnimationRainbowGradient, root.GetProperty("animation").GetByte());
            Assert.Equal(200, root.GetProperty("r").GetByte());
            Assert.Equal(100, root.GetProperty("g").GetByte());
            Assert.Equal(50, root.GetProperty("b").GetByte());
            Assert.Equal(90, root.GetProperty("brightness").GetByte());
        }
    }

    [Theory]
    [InlineData(0)]  // below FwAnimationColor
    [InlineData(5)]  // above FwAnimationRainbowGradient
    public async Task Put_RejectsAnimationOutOfRange(int animation)
    {
        var (factory, client) = Boot(NewConnectedHub());
        using (factory)
        {
            var res = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation, r = 1, g = 1, b = 1, brightness = 50 });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(256, 0, 0)]
    [InlineData(0, 256, 0)]
    [InlineData(0, 0, 256)]
    public async Task Put_RejectsColorOutOfRange(int r, int g, int b)
    {
        var (factory, client) = Boot(NewConnectedHub());
        using (factory)
        {
            var res = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation = QSeriesCoolerProtocol.FwAnimationColor, r, g, b, brightness = 50 });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Put_RejectsBrightnessOutOfRange(int brightness)
    {
        var (factory, client) = Boot(NewConnectedHub());
        using (factory)
        {
            var res = await client.PutAsJsonAsync("/devices/qseries/firmware-animation",
                new { animation = QSeriesCoolerProtocol.FwAnimationColor, r = 1, g = 1, b = 1, brightness });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    // Models a real MCU well enough for the route round-trip: a Port-0 query
    // returns the live 20-byte state, and an FF CC 0C write updates its [15..19]
    // firmware-animation block in place.
    private sealed class StatefulTransport : INp50Transport
    {
        private readonly byte[] _port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        private byte[]? _pending;

        public StatefulTransport()
        {
            _port0[0] = 0xFF;
            _port0[1] = 0xCC;
        }

        public bool IsOpen => true;
        public string Serial => "QINT123";

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xCC && data[2] == 0x01 && data[3] == 0x00)
            {
                _pending = (byte[])_port0.Clone();
                return;
            }
            if (data.Length == 9 && data[0] == 0xFF && data[1] == 0xCC && data[2] == 0x0C)
            {
                _port0[15] = data[3];
                _port0[16] = data[4];
                _port0[17] = data[5];
                _port0[18] = data[6];
                _port0[19] = data[7];
            }
            _pending = null;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_pending is null) return 0;
            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, n).CopyTo(buffer);
            _pending = null;
            return n;
        }

        public void DiscardInput() { }
        public void Dispose() { }
    }
}
