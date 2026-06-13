using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// WebSocket envelope serialization | the highest-frequency allocation path.
/// The "monitoring" composite frame ships once per tick (default 1 Hz, floor
/// 200 ms); per-topic frames (cpu, gpu, ...) ship when separately subscribed.
/// All route through <see cref="WsEnvelope.Build"/>.
///
/// <see cref="BuildMonitoring_DoublingBaseline"/> reproduces the pre-pool
/// implementation (256-byte ArrayBufferWriter, doubling growth) so the win from
/// the pooled-scratch <see cref="WsEnvelope.Build"/> stays measurable.
/// </summary>
[MemoryDiagnoser]
[InProcess]
public class JsonBroadcastBenchmarks
{
    private readonly MonitoringFrame _frame = Payloads.MonitoringFrame();
    private readonly HardwareComponent _cpu = Payloads.Component("cpu", "AMD Ryzen 9 9950X", 32);

    [Benchmark(Baseline = true)]
    public int BuildMonitoring_DoublingBaseline()
    {
        var bytes = BuildDoubling("monitoring", _frame, AppJsonContext.Default.MonitoringFrame);
        return bytes.Length;
    }

    [Benchmark]
    public int BuildMonitoring()
    {
        var bytes = WsEnvelope.Build("monitoring", _frame, AppJsonContext.Default.MonitoringFrame);
        return bytes.Length;
    }

    [Benchmark]
    public int BuildCpuTopic()
    {
        var bytes = WsEnvelope.Build("cpu", _cpu, AppJsonContext.Default.HardwareComponent);
        return bytes.Length;
    }

    // Pre-optimization implementation, kept only as the benchmark baseline.
    private static ReadOnlyMemory<byte> BuildDoubling<T>(string topic, T payload, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("t", topic);
        writer.WritePropertyName("d");
        JsonSerializer.Serialize(writer, payload, typeInfo);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenMemory;
    }
}
