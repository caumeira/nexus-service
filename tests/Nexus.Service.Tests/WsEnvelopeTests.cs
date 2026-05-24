using System.Text;
using System.Text.Json;
using Nexus.Service.Activity;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class WsEnvelopeTests
{
    [Fact]
    public void Wrap_ProducesValidEnvelope()
    {
        var payload = "{\"value\":42}"u8;
        var envelope = WsEnvelope.Wrap("test-topic", payload);
        var json = Encoding.UTF8.GetString(envelope.Span);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("test-topic", root.GetProperty("t").GetString());
        Assert.Equal(42, root.GetProperty("d").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Build_WithProcessFrame_ProducesValidEnvelope()
    {
        var frame = new ProcessFrame
        {
            Processes = new List<ProcessEntry>
            {
                new() { Name = "chrome", CpuPercent = 12.5, MemoryMb = 512 },
            },
            TotalCpu = 45.0,
            TotalMemoryPercent = 67.0,
        };

        var envelope = WsEnvelope.Build("processes", frame, AppJsonContext.Default.ProcessFrame);
        var json = Encoding.UTF8.GetString(envelope.Span);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("processes", root.GetProperty("t").GetString());
        var d = root.GetProperty("d");
        Assert.Equal(45.0, d.GetProperty("totalCpu").GetDouble());
        Assert.Equal("chrome", d.GetProperty("processes")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Build_WithMonitoringFrame_ProducesValidEnvelope()
    {
        var frame = new MonitoringFrame
        {
            CpuModel = "Intel i9-14900K",
            GpuModels = new List<string> { "NVIDIA RTX 4090" },
            MemoryTotal = "32.0 GB",
            MotherboardModel = "ASUS ROG",
            Cpu = new HardwareComponent
            {
                Id = "cpu",
                Name = "Intel i9-14900K",
                Sensors = new List<HardwareSensor>
                {
                    new() { Id = "/cpu/temp", Name = "CPU Package", Type = "Temperature", Value = 65.5f, Units = "°C", Formatted = "65.5 °C" },
                },
            },
            Processes = new ProcessFrame
            {
                Processes = new List<ProcessEntry>
                {
                    new() { Name = "game.exe", CpuPercent = 30.0, MemoryMb = 4096 },
                },
                TotalCpu = 55.0,
                TotalMemoryPercent = 72.0,
            },
            Network = new NetworkFrame
            {
                Entries = new List<NetworkRateEntry>
                {
                    new() { Name = "steam", RateIn = 1024000, RateOut = 5120 },
                },
            },
        };

        var envelope = WsEnvelope.Build("monitoring", frame, AppJsonContext.Default.MonitoringFrame);
        var json = Encoding.UTF8.GetString(envelope.Span);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("monitoring", root.GetProperty("t").GetString());
        var d = root.GetProperty("d");
        Assert.Equal("Intel i9-14900K", d.GetProperty("cpuModel").GetString());
        Assert.Equal(55.0, d.GetProperty("processes").GetProperty("totalCpu").GetDouble());
        Assert.Equal("steam", d.GetProperty("network").GetProperty("entries")[0].GetProperty("name").GetString());

        var cpuSensor = d.GetProperty("cpu").GetProperty("sensors")[0];
        Assert.Equal("Temperature", cpuSensor.GetProperty("type").GetString());
        Assert.InRange(cpuSensor.GetProperty("value").GetSingle(), 65.0f, 66.0f);
    }

    [Fact]
    public void Wrap_WithEmptyPayload_ProducesValidEnvelope()
    {
        var envelope = WsEnvelope.Wrap("empty", "{}"u8);
        var json = Encoding.UTF8.GetString(envelope.Span);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("empty", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("d").ValueKind);
    }
}
