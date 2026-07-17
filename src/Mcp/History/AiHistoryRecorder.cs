using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// Records a curated telemetry sensor set into <see cref="IAiHistoryStore"/> on
/// its own tick cadence, mirroring TemperatureSampler's
/// BackgroundService + PeriodicTimer + per-tick try/catch shape. Records only
/// while AiIntegration.Enabled - this is the master toggle, not the History
/// capability flag, which gates the read tools' access, not what gets recorded.
/// </summary>
public sealed class AiHistoryRecorder : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // Bounds the wait for ISensorProvider.ReadyAsync, same rationale as
    // TemperatureSampler.StartupReadyTimeout.
    private static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromSeconds(60);

    private readonly ISensorProvider _sensors;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly IAiHistoryStore _history;

    public AiHistoryRecorder(ISensorProvider sensors, IFanControlProvider fans, IConfigStore store, IAiHistoryStore history)
    {
        _sensors = sensors;
        _fans = fans;
        _store = store;
        _history = history;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _sensors.ReadyAsync(stoppingToken).WaitAsync(StartupReadyTimeout, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
        }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                RecordOnce(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[ai-history] tick failed: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Extracted so tests can drive one tick synchronously with a fixed
    /// clock instead of racing BackgroundService.StartAsync's async prefix.</summary>
    internal void RecordOnce(DateTime nowUtc)
    {
        if (!_history.IsAvailable)
        {
            return;
        }
        if (!_store.Load().AiIntegration.Enabled)
        {
            return;
        }

        var rows = CollectSamples(nowUtc);
        if (rows.Count > 0)
        {
            _history.RecordSamples(rows, nowUtc);
        }
    }

    internal List<AiHistorySampleRow> CollectSamples(DateTime nowUtc)
    {
        var tsUtcMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        var rows = new List<AiHistorySampleRow>();

        var cpuSensors = _sensors.GetCpuSensors();
        AddSensorRow(rows, PreferElseFirst(cpuSensors, "Temperature", "Package"), "temperature", tsUtcMs);
        AddSensorRow(rows, PreferElseFirst(cpuSensors, "Load", "CPU Total"), "load", tsUtcMs);
        AddSensorRow(rows, PreferElseFirst(cpuSensors, "Power", "Package"), "power", tsUtcMs);

        var gpuSensors = PrimaryGpuSensors();
        AddSensorRow(rows, PreferElseFirst(gpuSensors, "Temperature", "Core"), "temperature", tsUtcMs);
        AddSensorRow(rows, PreferElseFirst(gpuSensors, "Load", "Core"), "load", tsUtcMs);
        AddSensorRow(rows, FindSensor(gpuSensors, "Power", null), "power", tsUtcMs);

        foreach (var channel in _fans.GetFanChannels())
        {
            var kind = string.Equals(channel.Kind, FanKinds.Pump, StringComparison.Ordinal) ? "pump" : "fan";
            rows.Add(new AiHistorySampleRow(channel.Id, channel.Name, kind, "RPM", channel.Rpm, tsUtcMs));
        }

        foreach (var source in _fans.GetTemperatureSources())
        {
            if (source.Name.Contains("coolant", StringComparison.OrdinalIgnoreCase)
                || source.Name.Contains("liquid", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new AiHistorySampleRow(
                    source.Id, source.Name, "coolant", "C", SensorValueSanitizer.Sanitize(source.Value), tsUtcMs));
            }
        }

        return rows;
    }

    private static void AddSensorRow(List<AiHistorySampleRow> rows, HardwareSensor? sensor, string kind, long tsUtcMs)
    {
        if (sensor is null)
        {
            return;
        }
        rows.Add(new AiHistorySampleRow(
            sensor.Id, sensor.Name, kind, sensor.Units, SensorValueSanitizer.Sanitize(sensor.Value), tsUtcMs));
    }

    private IReadOnlyList<HardwareSensor> PrimaryGpuSensors()
    {
        var gpus = _sensors.GetGpus();
        foreach (var g in gpus)
        {
            if (!g.Integrated)
            {
                return g.Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static HardwareSensor? PreferElseFirst(IReadOnlyList<HardwareSensor> sensors, string type, string nameContains)
        => FindSensor(sensors, type, nameContains) ?? FindSensor(sensors, type, null);

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        foreach (var s in sensors)
        {
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }
}
