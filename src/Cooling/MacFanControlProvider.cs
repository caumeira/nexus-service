using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Cooling;

/// <summary>
/// macOS fan / temperature provider backed by IOKit AppleSMC. Reads real
/// hardware data: fan count via FNum, per-fan current RPM via F{n}Ac, limits
/// via F{n}Mn / F{n}Mx, and CPU / GPU die temperatures via the Tp / Tg / Te
/// (Apple Silicon) or TC0P / TG0P (Intel) keys. Write paths (SetFanSpeed,
/// ReleaseFan) are silent no-ops because target-RPM writes require SIP off
/// on Apple Silicon. Mode is reported as Auto so the UI does not imply
/// software control.
/// </summary>
public sealed class MacFanControlProvider : IFanControlProvider, ICoolingProvider, IDisposable
{
    private readonly MacSmc _smc = new();
    private readonly Func<string, float?> _readKey;
    private readonly int _fanCount;
    private bool _warnedOnWrite;

    public MacFanControlProvider()
    {
        _readKey = _smc.ReadFloatKey;
        _fanCount = _smc.IsOpen ? (_smc.ReadIntKey("FNum") ?? 0) : 0;
        if (!_smc.IsOpen)
            Console.Error.WriteLine("[cooling] AppleSMC unavailable - cooling reports no hardware");
    }

    public void Dispose() => _smc.Dispose();

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (_fanCount == 0) return Array.Empty<FanChannel>();
        var list = new List<FanChannel>(_fanCount);
        for (int i = 0; i < _fanCount; i++)
        {
            var rpm = (int)(_smc.ReadFloatKey($"F{i}Ac") ?? 0f);
            var min = (int)(_smc.ReadFloatKey($"F{i}Mn") ?? 0f);
            var max = (int)(_smc.ReadFloatKey($"F{i}Mx") ?? 0f);
            int duty;
            if (max > min)
                duty = (int)Math.Clamp((rpm - min) * 100.0 / (max - min), 0, 100);
            else
                duty = 0;
            list.Add(new FanChannel
            {
                Id = $"mac/fan/{i}",
                Name = NameFor(i, _fanCount),
                DutyPercent = duty,
                Rpm = rpm,
                Mode = FanModes.Auto,
            });
        }
        return list;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var sources = new List<TemperatureSource>();
        // Same disconnected-channel gate as the Linux/Windows providers - an SMC
        // die key that reads 0 (sensor absent on this Mac) shouldn't become a
        // curve input. See TemperatureSourceFilter.
        var cpu = MacSmcTemperatures.Average(_readKey, MacSmcTemperatures.CpuKeys);
        if (cpu.HasValue && TemperatureSourceFilter.IsPlausible(cpu.Value))
            sources.Add(new TemperatureSource { Id = "mac/cpu/die", Name = "CPU Die", Category = "CPU", Value = cpu.Value });
        var gpu = MacSmcTemperatures.Average(_readKey, MacSmcTemperatures.GpuKeys);
        if (gpu.HasValue && TemperatureSourceFilter.IsPlausible(gpu.Value))
            sources.Add(new TemperatureSource { Id = "mac/gpu/die", Name = "GPU Die", Category = "GPU", Value = gpu.Value });
        return sources;
    }

    public float? ReadTemperature(string sensorId) => sensorId switch
    {
        "mac/cpu/die" => MacSmcTemperatures.Average(_readKey, MacSmcTemperatures.CpuKeys),
        "mac/gpu/die" => MacSmcTemperatures.Average(_readKey, MacSmcTemperatures.GpuKeys),
        _ => null,
    };

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        if (!_warnedOnWrite)
        {
            _warnedOnWrite = true;
            Console.Error.WriteLine("[cooling] fan control writes are not supported on macOS - request ignored");
        }
        return Math.Clamp(dutyPercent, 0, 100);
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) => SetFanSpeed(channelId, dutyPercent);

    public void ReleaseFan(string channelId) { }
    public void ReleaseAll() { }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var channels = GetFanChannels();
        if (channels.Count == 0) return Array.Empty<CoolingComponent>();
        return new[]
        {
            new CoolingComponent
            {
                Id = "mac-fans",
                Name = channels.Count == 1 ? "Fan" : "Fans",
                Type = "Motherboard",
                Devices = channels.Select(ch => new CoolingDevice
                {
                    Id = ch.Id,
                    Name = ch.Name,
                    Type = "Fan",
                    Speed = ch.DutyPercent,
                    Rpm = ch.Rpm,
                    Pwm = ch.DutyPercent,
                }).ToList(),
            },
        };
    }

    private static string NameFor(int index, int count)
    {
        if (count == 1) return "Fan";
        if (count == 2) return index == 0 ? "Left Fan" : "Right Fan";
        return $"Fan {index + 1}";
    }
}
