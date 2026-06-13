using System;
using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

/// <summary>
/// Allocation regression guards for the service hot paths. Uses the thread-local
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counter, so the parallel
/// xUnit runner cannot pollute a measurement — only work on this thread counts.
/// Fast and deterministic, so these run in the default suite (no Manual gate).
///
/// Companion ns/op micro-benchmarks live in tests/Nexus.Service.Benchmarks
/// (BenchmarkDotNet, on-demand). These tests guard the allocation budgets those
/// benchmarks established.
/// </summary>
public class AllocationBudgetTests
{
    /// <summary>
    /// Runs <paramref name="body"/> once to force JIT/first-call setup, then
    /// measures the per-iteration managed allocation over a steady-state run.
    /// </summary>
    private static long BytesPerIteration(int iterations, Action body)
    {
        for (int i = 0; i < 50; i++)
        {
            body();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            body();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iterations;
    }

    // ── Lighting canvas: rewritten ~30 Hz per device, must never allocate ──

    [Fact]
    public void CanvasFill_IsZeroAlloc()
    {
        var canvas = new CanvasBuffer(160, 90);
        Assert.Equal(0, BytesPerIteration(1000, () => canvas.Fill(255, 128, 64)));
    }

    [Fact]
    public void CanvasClear_IsZeroAlloc()
    {
        var canvas = new CanvasBuffer(160, 90);
        Assert.Equal(0, BytesPerIteration(1000, () => canvas.Clear()));
    }

    [Fact]
    public void CanvasWriteFromRgb_IsZeroAlloc()
    {
        var canvas = new CanvasBuffer(160, 90);
        var src = new byte[160 * 90 * 3];
        Assert.Equal(0, BytesPerIteration(1000, () => canvas.WriteFromRgb(src)));
    }

    // ── Curve evaluation: once per fan channel per tick, must never allocate ──

    [Fact]
    public void EvaluateGraph_IsZeroAlloc()
    {
        var graph = new GraphCurveData { SpeedModifier = 1.0 };
        for (int i = 0; i < 8; i++)
        {
            graph.Points.Add(new GraphPoint { Temp = 20 + i * 7.0, Speed = 20 + i * 10.0 });
        }
        Assert.Equal(0, BytesPerIteration(1000, () => CurveEngine.EvaluateGraph(graph, 55f)));
    }

    // ── NP50 per-port frame assembly: ~30 Hz, writes a pre-allocated buffer ──

    [Fact]
    public void Np50FillBufferSlice_IsZeroAlloc()
    {
        const int leds = 120;
        var dst = new RgbColor[leds];
        var src = new byte[leds * 3];
        Assert.Equal(0, BytesPerIteration(1000,
            () => Np50LightingFrameWriter.FillBufferSlice(dst, 0, src, leds, 1.0, false, 0, 0)));
    }

    // ── Monitoring broadcast: ~1 Hz composite + per-topic sub-frames. ──
    // WsEnvelope.Build serializes into a pooled scratch and returns one
    // right-sized array. Ceiling guards against regressing back to the
    // doubling-growth garbage (which was ~64 KB/op for this frame).
    [Fact]
    public void BuildMonitoringEnvelope_StaysUnderCeiling()
    {
        var frame = SampleMonitoringFrame();
        long perOp = BytesPerIteration(200, () =>
        {
            var bytes = WsEnvelope.Build("monitoring", frame, AppJsonContext.Default.MonitoringFrame);
            GC.KeepAlive(bytes);
        });
        Assert.True(perOp < 32 * 1024, $"monitoring envelope allocated {perOp} B/op, ceiling is {32 * 1024} B");
    }

    private static MonitoringFrame SampleMonitoringFrame()
    {
        static HardwareComponent Component(string id, string name, int sensors)
        {
            var list = new List<HardwareSensor>(sensors);
            for (int i = 0; i < sensors; i++)
            {
                list.Add(new HardwareSensor
                {
                    Id = $"{id}.s{i}",
                    Name = $"{name} {i}",
                    Type = "Load",
                    Value = 42f + i,
                    Units = "%",
                    Formatted = $"{42 + i}%",
                    Parent = new SensorParent { Id = id, Name = name },
                });
            }
            return new HardwareComponent { Id = id, Name = name, Sensors = list };
        }

        return new MonitoringFrame
        {
            Cpu = Component("cpu", "CPU", 32),
            Gpu = new List<HardwareComponent> { Component("gpu0", "GPU", 12) },
            Memory = Component("mem", "Memory", 4),
            Motherboard = Component("mb", "Board", 10),
            CpuModel = "CPU",
            MemoryTotal = "64 GB",
            MotherboardModel = "Board",
        };
    }
}
