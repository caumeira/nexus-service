using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// NVIDIA GPU fan + temperature provider backed by NVML (libnvidia-ml) — the
/// same library CoolerControl uses, which (unlike <c>nvidia-smi</c>) exposes
/// each fan individually. A dual-fan card therefore shows two channels.
/// Reading works as the unprivileged service user; control
/// (<c>nvmlDeviceSetFanSpeed_v2</c>) requires root, so on a normal user session
/// a write is attempted and logged-once when the driver returns no-permission.
///
/// AMD GPUs are intentionally NOT handled here — the amdgpu driver exposes its
/// fan through hwmon, so <see cref="LinuxFanControlProvider"/> already covers it.
///
/// Channel ids are <c>nvidia:&lt;gpu&gt;:&lt;fan&gt;</c>; the composite routes by
/// the <c>nvidia:</c> prefix and skips calibration (NVML reports duty %, not a
/// stable RPM to ramp against). The NVML interop is wrapped so a box without the
/// NVIDIA driver (DllNotFound) degrades cleanly to no GPU fans.
/// </summary>
public sealed class LinuxNvidiaFanProvider : IFanControlProvider, ICoolingProvider
{
    public const string IdPrefix = "nvidia:";
    private const string TempPrefix = "nvidia:temp:";

    private readonly Func<List<GpuInfo>> _read;
    private readonly Func<int, int, int?, bool> _control; // (gpu, fan, duty | null=auto) -> applied
    private readonly bool _forceAvailable;

    private readonly object _lock = new();
    private readonly HashSet<string> _warned = new();
    // NVML enumeration costs a few ms; one curve tick reads fans + temps. Cache.
    private const long SnapshotTtlMs = 800;
    private List<GpuInfo>? _snapshot;
    private long _snapshotAtMs = long.MinValue;

    public LinuxNvidiaFanProvider()
    {
        _read = Nvml.Read;
        _control = Nvml.SetFan;
        _forceAvailable = false;
    }

    internal LinuxNvidiaFanProvider(Func<List<GpuInfo>> read, Func<int, int, int?, bool> control)
    {
        _read = read;
        _control = control;
        _forceAvailable = true;
    }

    private bool Available() => _forceAvailable || (OperatingSystem.IsLinux() && Nvml.Available);

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var channels = new List<FanChannel>();
        foreach (var g in Snapshot())
        {
            var deviceId = IdPrefix + g.Index.ToString(CultureInfo.InvariantCulture);
            foreach (var f in g.Fans)
            {
                channels.Add(new FanChannel
                {
                    Id = $"{deviceId}:{f.Fan.ToString(CultureInfo.InvariantCulture)}",
                    Name = g.Fans.Count > 1 ? $"{g.Name} fan {f.Fan + 1}" : $"{g.Name} fan",
                    DutyPercent = f.Duty,
                    Rpm = 0, // NVML consumer API exposes duty %, not tach RPM
                    Mode = FanModes.Auto,
                    DeviceId = deviceId,
                    DeviceName = g.Name,
                });
            }
        }
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var sources = new List<TemperatureSource>();
        foreach (var g in Snapshot())
        {
            if (g.Temp is null)
                continue;
            sources.Add(new TemperatureSource
            {
                Id = TempPrefix + g.Index.ToString(CultureInfo.InvariantCulture),
                Name = $"{g.Name} temp",
                Category = "GPU",
                Value = g.Temp.Value,
            });
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!sensorId.StartsWith(TempPrefix, StringComparison.Ordinal))
            return null;
        if (!int.TryParse(sensorId.Substring(TempPrefix.Length), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var idx))
            return null;
        return Snapshot().FirstOrDefault(g => g.Index == idx)?.Temp;
    }

    public int SetFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent);
    public void DriveFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent);

    private int Drive(string channelId, int dutyPercent)
    {
        dutyPercent = Math.Clamp(dutyPercent, 0, 100);
        if (TryParseFan(channelId, out var gpu, out var fan) && !_control(gpu, fan, dutyPercent))
            WarnOnce(channelId);
        return dutyPercent;
    }

    public void ReleaseFan(string channelId)
    {
        if (TryParseFan(channelId, out var gpu, out var fan))
            _control(gpu, fan, null);
    }

    public void ReleaseAll()
    {
        foreach (var g in Snapshot())
            foreach (var f in g.Fans)
                _control(g.Index, f.Fan, null);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var gpus = Snapshot();
        if (gpus.Count == 0)
            return Array.Empty<CoolingComponent>();
        return gpus.Select(g => new CoolingComponent
        {
            Id = IdPrefix + g.Index,
            Name = g.Name,
            Type = "GPU",
            Devices = g.Fans.Select(f => new CoolingDevice
            {
                Id = $"{IdPrefix}{g.Index}:{f.Fan}",
                Name = g.Fans.Count > 1 ? $"{g.Name} fan {f.Fan + 1}" : $"{g.Name} fan",
                Type = "Fan",
                Speed = f.Duty,
                Pwm = f.Duty,
                Temperature = g.Temp,
            }).ToList(),
        }).ToList();
    }

    public static bool IsNvidiaId(string id)
        => !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    private List<GpuInfo> Snapshot()
    {
        if (!Available())
            return new List<GpuInfo>();
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_snapshot is not null && now - _snapshotAtMs < SnapshotTtlMs)
                return _snapshot;
            _snapshot = _read();
            _snapshotAtMs = now;
            return _snapshot;
        }
    }

    // "nvidia:0:1" -> gpu 0, fan 1. Rejects "nvidia:temp:0" (non-numeric gpu).
    private static bool TryParseFan(string id, out int gpu, out int fan)
    {
        gpu = fan = -1;
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return false;
        var parts = id.Split(':');
        return parts.Length == 3
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out gpu)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fan);
    }

    private void WarnOnce(string channelId)
    {
        lock (_lock)
        {
            if (!_warned.Add(channelId))
                return;
        }
        Console.Error.WriteLine(
            $"[cooling] nvidia fan write for {channelId} was rejected — NVML fan control needs root. " +
            "GPU stays on its automatic curve.");
    }
}

/// <summary>One GPU's fan/thermal snapshot. Duty is the NVML fan speed %.</summary>
internal sealed record GpuInfo(int Index, string Name, float? Temp, List<GpuFan> Fans);
internal readonly record struct GpuFan(int Fan, int Duty);
