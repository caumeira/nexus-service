using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// USB liquid-cooler / AIO / fan-hub provider backed by the
/// <a href="https://github.com/liquidctl/liquidctl">liquidctl</a> CLI — the same
/// universal driver library CoolerControl shells out to. Covers NZXT Kraken,
/// Corsair Commander/AIOs, EVGA CLC, Aquacomputer, and the rest of liquidctl's
/// device list without Nexus reimplementing each USB protocol. Reads via
/// <c>liquidctl --json status</c>; controls via <c>liquidctl --address … set
/// &lt;channel&gt; speed &lt;duty&gt;</c>.
///
/// Subprocess-based (AOT-safe, no native deps) through <see cref="ShellExecutor"/>.
/// When liquidctl isn't installed every method degrades to empty — the provider
/// is inert, never throws. Channel ids are <c>liquidctl:&lt;addr&gt;:&lt;channel&gt;</c>;
/// the composite routes by that prefix. These are USB coolers with no BIOS
/// fallback, so <see cref="ReleaseFan"/> is a no-op and calibration is skipped.
/// </summary>
public sealed class LinuxLiquidctlProvider : IFanControlProvider, ICoolingProvider
{
    public const string IdPrefix = "liquidctl:";

    private readonly Func<string> _statusJson;
    private readonly Action<string, string, int> _setSpeed; // (address, channel, duty)
    private readonly bool _forceAvailable;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Address, string Channel)> _control = new();
    private readonly Dictionary<string, string> _tempAddr = new();
    private bool? _present;

    // `liquidctl --json status` enumerates USB and costs ~1-2s; one curve tick
    // reads fans + temps. Cache the parse briefly so a tick spawns it once.
    private const long SnapshotTtlMs = 1500;
    private List<LiquidDevice>? _snapshot;
    private long _snapshotAtMs = long.MinValue;

    public LinuxLiquidctlProvider()
    {
        _statusJson = () => ShellExecutor.Run("liquidctl", 8000, "--json", "status");
        _setSpeed = (addr, chan, duty) => ShellExecutor.Run("liquidctl", 8000,
            "--address", addr, "set", chan, "speed", duty.ToString(CultureInfo.InvariantCulture));
        _forceAvailable = false;
    }

    // Injected command seams for tests (no real liquidctl needed).
    internal LinuxLiquidctlProvider(Func<string> statusJson, Action<string, string, int> setSpeed)
    {
        _statusJson = statusJson;
        _setSpeed = setSpeed;
        _forceAvailable = true;
    }

    private bool Available()
    {
        if (_forceAvailable)
            return true;
        if (!OperatingSystem.IsLinux())
            return false;
        // Cache only a positive probe — liquidctl may be installed after us.
        if (_present == true)
            return true;
        var ok = !string.IsNullOrWhiteSpace(ShellExecutor.Run("liquidctl", "--version"));
        if (ok) _present = true;
        return ok;
    }

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var devices = Snapshot();
        var channels = new List<FanChannel>();
        lock (_lock) _control.Clear();
        foreach (var dev in devices)
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            foreach (var ch in dev.Channels)
            {
                var id = $"{deviceId}:{ch.Name}";
                lock (_lock) _control[id] = (dev.Address, ch.Name);
                channels.Add(new FanChannel
                {
                    Id = id,
                    Name = $"{dev.Description} {ch.Name}",
                    DutyPercent = ch.Duty ?? 0,
                    Rpm = ch.Rpm ?? 0,
                    Mode = ch.Duty is not null ? FanModes.Manual : FanModes.Auto,
                    DeviceId = deviceId,
                    DeviceName = dev.Description,
                    PortLabel = ch.Name,
                });
            }
        }
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var devices = Snapshot();
        var sources = new List<TemperatureSource>();
        lock (_lock) _tempAddr.Clear();
        foreach (var dev in devices)
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            for (var i = 0; i < dev.Temps.Count; i++)
            {
                var t = dev.Temps[i];
                var id = $"{deviceId}:temp{i}";
                lock (_lock) _tempAddr[id] = dev.Address;
                sources.Add(new TemperatureSource
                {
                    Id = id,
                    Name = $"{dev.Description} {t.Label}",
                    Category = "Hub",
                    Value = t.Celsius,
                    DeviceId = deviceId,
                });
            }
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        // Re-snapshot and find the matching label index. Cheap enough; AIO temps
        // are read on the curve-engine tick which is already throttled.
        var devices = Snapshot();
        foreach (var dev in devices)
        {
            var deviceId = IdPrefix + Sanitize(dev.Address);
            for (var i = 0; i < dev.Temps.Count; i++)
                if (sensorId == $"{deviceId}:temp{i}")
                    return dev.Temps[i].Celsius;
        }
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent);

    public void DriveFanSpeed(string channelId, int dutyPercent) => Drive(channelId, dutyPercent);

    private int Drive(string channelId, int dutyPercent)
    {
        dutyPercent = Math.Clamp(dutyPercent, 0, 100);
        (string Address, string Channel) target;
        lock (_lock)
        {
            if (!_control.TryGetValue(channelId, out target))
            {
                // Cold cache (process restart): rebuild the map then retry.
                GetFanChannels();
                if (!_control.TryGetValue(channelId, out target))
                    return dutyPercent;
            }
        }
        _setSpeed(target.Address, target.Channel, dutyPercent);
        return dutyPercent;
    }

    // USB AIOs have no BIOS/automatic mode to hand control back to — leave the
    // last applied duty in place rather than guess a "default".
    public void ReleaseFan(string channelId) { }
    public void ReleaseAll() { }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var devices = Snapshot();
        if (devices.Count == 0)
            return Array.Empty<CoolingComponent>();
        return devices.Select(dev => new CoolingComponent
        {
            Id = IdPrefix + Sanitize(dev.Address),
            Name = dev.Description,
            Type = "MiniHub",
            Devices = dev.Channels.Select(ch => new CoolingDevice
            {
                Id = $"{IdPrefix}{Sanitize(dev.Address)}:{ch.Name}",
                Name = ch.Name,
                Type = ch.Name.StartsWith("pump", StringComparison.Ordinal) ? "Pump" : "Fan",
                Speed = ch.Duty,
                Rpm = ch.Rpm,
                Pwm = ch.Duty,
            }).ToList(),
        }).ToList();
    }

    private List<LiquidDevice> Snapshot()
    {
        if (!Available())
            return new List<LiquidDevice>();
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_snapshot is not null && now - _snapshotAtMs < SnapshotTtlMs)
                return _snapshot;
            _snapshot = ParseStatus(_statusJson());
            _snapshotAtMs = now;
            return _snapshot;
        }
    }

    public static bool IsLiquidctlId(string id)
        => !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ── parsing (pure, unit-tested) ──

    internal sealed record LiquidDevice(string Address, string Description,
        List<LiquidChannel> Channels, List<LiquidTemp> Temps);
    internal sealed record LiquidChannel(string Name, int? Rpm, int? Duty);
    internal sealed record LiquidTemp(string Label, float Celsius);

    /// <summary>
    /// Parse <c>liquidctl --json status</c> output. Each device's flat status
    /// list is folded into named channels (fan / fanN / pump) by pairing the
    /// "… speed" (rpm) and "… duty" (%) rows, plus any "… temperature" rows.
    /// </summary>
    internal static List<LiquidDevice> ParseStatus(string json)
    {
        var result = new List<LiquidDevice>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return result; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var devEl in doc.RootElement.EnumerateArray())
            {
                var address = GetStr(devEl, "address");
                var description = GetStr(devEl, "description");
                if (string.IsNullOrEmpty(address))
                    continue;

                // Channel name -> (rpm, duty), preserving first-seen order.
                var chans = new List<string>();
                var rpm = new Dictionary<string, int>();
                var duty = new Dictionary<string, int>();
                var temps = new List<LiquidTemp>();

                if (devEl.TryGetProperty("status", out var statusEl)
                    && statusEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in statusEl.EnumerateArray())
                    {
                        var key = GetStr(row, "key");
                        if (string.IsNullOrEmpty(key) || !row.TryGetProperty("value", out var valEl))
                            continue;
                        if (!TryNumber(valEl, out var num))
                            continue;
                        var keyLower = key.ToLowerInvariant();

                        if (keyLower.Contains("temp"))
                        {
                            temps.Add(new LiquidTemp(key, (float)num));
                            continue;
                        }
                        var channel = ChannelName(keyLower);
                        if (channel is null)
                            continue;
                        if (!rpm.ContainsKey(channel) && !duty.ContainsKey(channel))
                            chans.Add(channel);
                        if (keyLower.Contains("duty"))
                            duty[channel] = (int)Math.Round(num);
                        else if (keyLower.Contains("speed") || keyLower.Contains("rpm"))
                            rpm[channel] = (int)Math.Round(num);
                    }
                }

                var channelList = chans
                    .Select(c => new LiquidChannel(c,
                        rpm.TryGetValue(c, out var r) ? r : null,
                        duty.TryGetValue(c, out var d) ? d : null))
                    .ToList();
                result.Add(new LiquidDevice(address, string.IsNullOrEmpty(description) ? address : description,
                    channelList, temps));
            }
        }
        return result;
    }

    // "Fan speed" -> "fan", "Fan 1 duty" -> "fan1", "Pump speed" -> "pump".
    // Returns null for non-fan/pump rows (voltages, firmware, etc.).
    private static string? ChannelName(string keyLower)
    {
        if (keyLower.Contains("pump"))
            return "pump";
        var fan = keyLower.IndexOf("fan", StringComparison.Ordinal);
        if (fan < 0)
            return null;
        var digits = new string(keyLower.Skip(fan + 3)
            .SkipWhile(c => c == ' ')
            .TakeWhile(char.IsDigit)
            .ToArray());
        return "fan" + digits;
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool TryNumber(JsonElement el, out double value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static string Sanitize(string raw)
    {
        var chars = raw.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
