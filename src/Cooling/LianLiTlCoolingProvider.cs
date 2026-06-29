using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Lian Li Uni Fan TL hub into the fan-control subsystem.
/// Channel IDs are "lianli-tl:p{port}f{fan}" (e.g. "lianli-tl:p0f0").
/// </summary>
public sealed class LianLiTlCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly TlFanHub _hub;

    private readonly object _ctrlLock = new();
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingDuty = new(StringComparer.Ordinal);

    public LianLiTlCoolingProvider(TlFanHub hub)
    {
        _hub = hub;
    }

    public static bool IsLianLiTlId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli-tl:", StringComparison.Ordinal);

    // IFanControlProvider

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<FanChannel>();
        }
        // Read snapshot once; all array accesses use this single reference.
        var snap = _hub.Snapshot;
        int count = snap.ChannelCount;
        var result = new List<FanChannel>(count);
        for (int i = 0; i < count; i++)
        {
            int port = snap.Port[i];
            int fan = snap.FanIndex[i];
            var id = MakeChannelId(port, fan);
            int duty;
            bool sw;
            lock (_ctrlLock)
            {
                _pendingDuty.TryGetValue(id, out duty);
                sw = _softwareControlled.Contains(id);
            }
            result.Add(new FanChannel
            {
                Id = id,
                Name = $"Uni Fan TL Port {port + 1} Fan {fan + 1}",
                DutyPercent = duty,
                Rpm = snap.Rpm[i] >= 0 ? snap.Rpm[i] : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
                DeviceId = "lianli-tl",
                DeviceName = "Lian Li Uni Fan TL",
                PortLabel = $"Port {port + 1}",
                FanModel = null,
                Orientation = null,
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        int clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsLianLiTlId(channelId))
        {
            return;
        }
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            _pendingDuty.Remove(channelId);
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            _pendingDuty.Clear();
        }
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ICoolingProvider

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<CoolingComponent>();
        }
        var snap = _hub.Snapshot;
        int count = snap.ChannelCount;
        var devices = new List<CoolingDevice>(count);
        for (int i = 0; i < count; i++)
        {
            int port = snap.Port[i];
            int fan = snap.FanIndex[i];
            var id = MakeChannelId(port, fan);
            int duty;
            lock (_ctrlLock)
            {
                _pendingDuty.TryGetValue(id, out duty);
            }
            devices.Add(new CoolingDevice
            {
                Id = id,
                Name = $"Uni Fan TL Port {port + 1} Fan {fan + 1}",
                Type = "Fan",
                Rpm = snap.Rpm[i] >= 0 ? snap.Rpm[i] : 0,
                Pwm = duty,
            });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = "lianli-tl",
                Name = "Lian Li Uni Fan TL",
                Type = "LianLiTlHub",
                Devices = devices,
            },
        };
    }

    // Called by the connection worker after each successful RPM poll.
    public void ReassertControl()
    {
        if (!_hub.IsConnected)
        {
            return;
        }
        string[] ids;
        int[] duties;
        lock (_ctrlLock)
        {
            if (_softwareControlled.Count == 0)
            {
                return;
            }
            ids = new string[_softwareControlled.Count];
            duties = new int[_softwareControlled.Count];
            int idx = 0;
            foreach (var id in _softwareControlled)
            {
                ids[idx] = id;
                _pendingDuty.TryGetValue(id, out duties[idx]);
                idx++;
            }
        }
        // Read snapshot once for the whole re-assert pass.
        var snap = _hub.Snapshot;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!TryParseAddress(ids[i], out int port, out int fan))
            {
                continue;
            }
            int ch = snap.GetChannelIndex(port, fan);
            if (ch < 0)
            {
                continue;
            }
            if (!_hub.SetSpeed(ch, duties[i]))
            {
                ServiceLog.Warn($"[lianli-tl-cooling] ReassertControl {ids[i]} duty {duties[i]} returned false");
            }
        }
    }

    private void ApplyChannelWrite(string channelId, int duty)
    {
        if (!IsLianLiTlId(channelId))
        {
            return;
        }
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[lianli-tl-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        if (!TryParseAddress(channelId, out int port, out int fan))
        {
            return;
        }
        int ch = _hub.Snapshot.GetChannelIndex(port, fan);
        if (ch < 0)
        {
            return;
        }
        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            _pendingDuty[channelId] = duty;
        }
        if (!_hub.SetSpeed(ch, duty))
        {
            ServiceLog.Warn($"[lianli-tl-cooling] SetSpeed {channelId} duty {duty} returned false");
        }
    }

    private static string MakeChannelId(int port, int fanIndex) =>
        $"lianli-tl:p{port}f{fanIndex}";

    // Parses "lianli-tl:p{N}f{M}" into port and fanIndex.
    private static bool TryParseAddress(string channelId, out int port, out int fanIndex)
    {
        port = 0;
        fanIndex = 0;
        int colon = channelId.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }
        var seg = channelId.AsSpan(colon + 1);
        if (!seg.StartsWith("p", StringComparison.Ordinal))
        {
            return false;
        }
        seg = seg.Slice(1);
        int fIdx = seg.IndexOf('f');
        if (fIdx <= 0)
        {
            return false;
        }
        if (!int.TryParse(seg.Slice(0, fIdx), out port))
        {
            return false;
        }
        if (!int.TryParse(seg.Slice(fIdx + 1), out fanIndex))
        {
            return false;
        }
        return port >= 0 && fanIndex >= 0;
    }
}
