using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests;

public class OtaDfuEntryTests
{
    private static readonly byte[] DfuMagic = { 0xFF, 0xAA, 0x09, 0x08, 0x07, 0x06, 0x05 };

    [Theory]
    [InlineData(0x0400, new byte[] { 0xFF, 0xDC, 0x06, 0x04, 0x00, 0x00, 0xDD })] // Q60
    [InlineData(0x0403, new byte[] { 0xFF, 0xDC, 0x06, 0x04, 0x03, 0x00, 0xDD })] // Q80
    [InlineData(0x0901, new byte[] { 0xFF, 0xDC, 0x06, 0x09, 0x01, 0x00, 0xDD })] // NP50
    [InlineData(0x0B00, new byte[] { 0xFF, 0xDC, 0x06, 0x0B, 0x00, 0x00, 0xDD })] // CNVS Left
    [InlineData(0x0C01, new byte[] { 0xFF, 0xDC, 0x06, 0x0C, 0x01, 0x00, 0xDD })] // Y70 Infinite
    public void ProductKey_is_derived_from_the_pid(int pid, byte[] expected)
    {
        Assert.Equal(expected, OtaProductKey.ForProductId(pid));
    }

    [Fact]
    public void Enter_writes_key_verifies_then_sends_the_dfu_magic()
    {
        var key = OtaProductKey.ForProductId(0x0B00); // CNVS Left
        var t = new FakeTransport();
        // FF DC 07 readback echoes the stored key in bytes [3..6].
        t.QueueRead(new byte[] { 0xFF, 0xDC, 0x07, 0x0B, 0x00, 0x00, 0xDD });

        var ok = OtaDfuEntry.Enter(t, key);

        Assert.True(ok);
        Assert.Equal(DfuMagic, t.Writes[^1]);          // last thing on the wire is the reboot magic
        Assert.Contains(t.Writes, w => w.SequenceEqual(key)); // key was written
    }

    [Fact]
    public void Enter_refuses_to_send_the_magic_when_the_key_cannot_be_verified()
    {
        var key = OtaProductKey.ForProductId(0x0B00);
        var t = new FakeTransport(); // no canned reads -> readback always fails

        var ok = OtaDfuEntry.Enter(t, key);

        Assert.False(ok);
        Assert.DoesNotContain(t.Writes, w => w.SequenceEqual(DfuMagic)); // never rebooted into DFU
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        private readonly Queue<byte[]> _reads = new();

        public void QueueRead(byte[] response) => _reads.Enqueue(response);

        public bool IsOpen => true;
        public string Serial => "fake";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_reads.Count == 0) return 0;
            var r = _reads.Dequeue();
            var n = Math.Min(r.Length, buffer.Length);
            r.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Dispose() { }
    }
}
