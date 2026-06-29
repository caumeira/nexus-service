using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Strimer;

namespace Nexus.Service.Tests.Strimer;

internal sealed class StrimerTransportSpy : IHidDevice
{
    public readonly record struct Call(bool IsSetFeature, byte[] Bytes);
    public List<Call> Calls { get; } = new();

    public int    VendorId  => StrimerProtocol.VendorId;
    public int    ProductId => StrimerProtocol.ProductId;
    public string Path      => "spy";
    public string? Serial   => null;
    public int    UsagePage => StrimerProtocol.VendorUsagePage;
    public int    Usage     => StrimerProtocol.VendorUsage;

    public bool SetFeature(ReadOnlySpan<byte> report)   { Calls.Add(new Call(true,  report.ToArray())); return true; }
    public bool Write(ReadOnlySpan<byte> report)         { Calls.Add(new Call(false, report.ToArray())); return true; }
    public bool GetFeature(Span<byte> buffer)            => false;
    public bool GetInputReport(Span<byte> buffer)        => false;
    public bool SetOutputReport(ReadOnlySpan<byte> rpt)  => false;
    public int  Read(Span<byte> buffer, int timeoutMs)   => 0;
    public void Dispose() { }
}

public class StrimerHubTests
{
    // ── Transport routing ──

    [Fact]
    public void SendColorData_sends_via_Write()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);

        hub.SendColorData(0, ReadOnlySpan<byte>.Empty);

        Assert.Single(spy.Calls);
        Assert.False(spy.Calls[0].IsSetFeature, "color data must be Write (interrupt-OUT)");
    }

    [Fact]
    public void SendEffectCommit_sends_via_Write()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);

        hub.SendEffectCommit(0, 0x05, 0x00, 0x01, 0x02);

        Assert.Single(spy.Calls);
        Assert.False(spy.Calls[0].IsSetFeature, "effect commit must be Write (interrupt-OUT)");
    }

    [Fact]
    public void SendApplyLatch_sends_via_Write()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);

        hub.SendApplyLatch();

        Assert.Single(spy.Calls);
        Assert.False(spy.Calls[0].IsSetFeature, "apply latch must be Write (interrupt-OUT)");
    }

    [Fact]
    public void SendApplyLatch_writes_correct_bytes()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);

        hub.SendApplyLatch();

        var bytes = spy.Calls[0].Bytes;
        Assert.Equal(0xE0, bytes[0]);
        Assert.Equal(0x2C, bytes[1]);
        Assert.Equal(0x0F, bytes[2]);
        Assert.Equal(0xFF, bytes[3]);
        Assert.Equal(0x00, bytes[4]);
    }

    // ── Connection state ──

    [Fact]
    public void Detach_clears_IsConnected()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);
        Assert.True(hub.IsConnected);

        hub.Detach();

        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void SendColorData_returns_false_when_not_attached()
    {
        var hub = new StrimerHub();
        Assert.False(hub.SendColorData(0, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsConnected_is_true_after_attach()
    {
        var spy = new StrimerTransportSpy();
        var hub = new StrimerHub();
        hub.Attach(spy);
        Assert.True(hub.IsConnected);
    }
}
