using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport, Np50PortInfo
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Covers <see cref="QSeriesCoolerHub.TryReadFirmwareAnimation"/> and
/// <see cref="QSeriesCoolerHub.SetFirmwareAnimation"/> against a scripted Port-0
/// transport - the ROM-write skip, the write + readback-verify path, and the
/// not-connected precondition. The mismatch path asserts observable hub behaviour
/// (still one write, still returns true); the warning it logs is not asserted -
/// ServiceLog is a process-global tee unsuited to per-test capture.
/// </summary>
public class QSeriesCoolerFirmwareAnimationHubTests
{
    private static readonly byte[] Port0Query = { 0xFF, 0xCC, 0x01, 0x00 };

    private static QSeriesCoolerHub NewConnectedHub(out ScriptedTransport transport)
    {
        var t = new ScriptedTransport();
        transport = t;
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        // Above the Q60 FwAnimation/FwAnimationBrightness thresholds so SetFirmwareAnimation's
        // SupportsFirmwareAnimation gate does not itself block these tests.
        hub.State.FirmwareVersion = "2.0.9.1";
        return hub;
    }

    private static byte[] BuildPort0WithAnimation(byte anim, byte r, byte g, byte b, byte brightness)
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[15] = anim; resp[16] = r; resp[17] = g; resp[18] = b; resp[19] = brightness;
        return resp;
    }

    [Fact]
    public void TryReadFirmwareAnimation_parses_current_port0_state()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationRainbow, 1, 2, 3, 40));

        var result = hub.TryReadFirmwareAnimation();

        Assert.NotNull(result);
        Assert.Equal(QSeriesCoolerProtocol.FwAnimationRainbow, result!.Value.Animation);
        Assert.Equal(1, result.Value.R);
        Assert.Equal(2, result.Value.G);
        Assert.Equal(3, result.Value.B);
        Assert.Equal(40, result.Value.Brightness);
    }

    [Fact]
    public void TryReadFirmwareAnimation_returns_null_when_not_connected()
    {
        var hub = new QSeriesCoolerHub(new FakeDiscovery(), _ => new ScriptedTransport());
        Assert.Null(hub.TryReadFirmwareAnimation());
    }

    [Fact]
    public void SetFirmwareAnimation_skips_the_ROM_write_when_the_block_is_already_identical()
    {
        var hub = NewConnectedHub(out var t);
        // Read-before-write reply reports the exact block being requested.
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationColor, 10, 20, 30, 80));

        var ok = hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationColor, 10, 20, 30, 80);

        Assert.True(ok);
        Assert.DoesNotContain(t.Writes, w => w.Length > 2 && w[1] == 0xCC && w[2] == 0x0C);
        // Only the one Port-0 query went out - no readback-verify round-trip either.
        Assert.Single(t.Writes, w => w.AsSpan().SequenceEqual(Port0Query));
    }

    [Fact]
    public void SetFirmwareAnimation_writes_once_and_verifies_the_readback_when_the_block_changed()
    {
        var hub = NewConnectedHub(out var t);
        // First Port-0 read (before write): old state. Second (after write): matches the write.
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationBreathe, 1, 1, 1, 50));
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationRainbowGradient, 200, 100, 50, 90));

        var ok = hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationRainbowGradient, 200, 100, 50, 90);

        Assert.True(ok);
        var mcuWrites = t.Writes.Where(w => w.Length > 2 && w[1] == 0xCC && w[2] == 0x0C).ToList();
        Assert.Single(mcuWrites);
        Assert.Equal(
            new byte[] { 0xFF, 0xCC, 0x0C, QSeriesCoolerProtocol.FwAnimationRainbowGradient, 200, 100, 50, 90, 0x01 },
            mcuWrites[0]);
        // Read-before-write + readback-verify: two Port-0 queries.
        Assert.Equal(2, t.Writes.Count(w => w.AsSpan().SequenceEqual(Port0Query)));
    }

    [Fact]
    public void SetFirmwareAnimation_still_reports_success_when_the_readback_disagrees()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationColor, 0, 0, 0, 0));
        // Readback reports something other than what was written (firmware did not take it).
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationColor, 0, 0, 0, 0));

        var ok = hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationBreathe, 5, 6, 7, 25);

        Assert.True(ok);
        Assert.Single(t.Writes, w => w.Length > 2 && w[1] == 0xCC && w[2] == 0x0C);
    }

    [Fact]
    public void SetFirmwareAnimation_never_issues_more_than_one_ROM_write_per_call()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationColor, 0, 0, 0, 0));
        t.Port0Responses.Enqueue(BuildPort0WithAnimation(QSeriesCoolerProtocol.FwAnimationRainbow, 9, 9, 9, 9));

        hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationRainbow, 9, 9, 9, 9);

        Assert.Equal(1, t.Writes.Count(w => w.Length > 2 && w[1] == 0xCC && w[2] == 0x0C));
    }

    [Fact]
    public void SetFirmwareAnimation_returns_false_when_not_connected()
    {
        var hub = new QSeriesCoolerHub(new FakeDiscovery(), _ => new ScriptedTransport());
        Assert.False(hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationColor, 1, 2, 3, 4));
    }

    [Fact]
    public void SetFirmwareAnimation_returns_false_when_firmware_predates_support()
    {
        var hub = NewConnectedHub(out var t);
        hub.State.FirmwareVersion = "1.9.9.9"; // below the Q60 FwAnimation threshold

        var ok = hub.SetFirmwareAnimation(QSeriesCoolerProtocol.FwAnimationColor, 1, 2, 3, 4);

        Assert.False(ok);
        Assert.Empty(t.Writes);
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    // Answers each Port-0 query (FF CC 01 00) with the next queued response, in
    // order - lets a test script the read-before-write reply and the separate
    // readback-verify reply independently. Every write (including the queries
    // themselves) is recorded so a test can isolate the FF CC 0C MCU write.
    private sealed class ScriptedTransport : INp50Transport
    {
        private byte[]? _pending;
        public Queue<byte[]> Port0Responses { get; } = new();
        public List<byte[]> Writes { get; } = new();
        public bool IsOpen => true;
        public string Serial => "QTEST123";

        public void Write(ReadOnlySpan<byte> data)
        {
            var bytes = data.ToArray();
            Writes.Add(bytes);
            var isPort0Query = bytes.AsSpan().SequenceEqual(Port0Query);
            _pending = isPort0Query && Port0Responses.Count > 0 ? Port0Responses.Dequeue() : null;
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
