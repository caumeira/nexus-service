using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Cooling;

/// <summary>
/// Linux motherboard fan / temperature provider backed by the kernel hwmon
/// sysfs tree (<c>/sys/class/hwmon/hwmonX/</c>), the same source
/// <see cref="Nexus.Service.Sensors.LinuxSensorProvider"/> reads. A fan is
/// "controllable" when its chip exposes both <c>pwmN</c> and <c>pwmN_enable</c>;
/// duty is the 0-255 pwm value scaled to 0-100, RPM comes from the paired
/// <c>fanN_input</c>. Writes set <c>pwmN_enable=1</c> (manual) then <c>pwmN</c>;
/// release restores <c>pwmN_enable=2</c> (automatic). Pure sysfs file IO via
/// <see cref="LinuxSysfs"/> — AOT-safe, no P/Invoke. Only DI-wired on Linux; on
/// any other OS the hwmon root is absent so enumeration simply yields nothing.
/// Writing pwm needs access to the hwmon attributes (see the bundled udev rule);
/// failures are swallowed (logged once) so a locked-down box degrades to read-only.
/// </summary>
public sealed class LinuxFanControlProvider : IFanControlProvider, ICoolingProvider
{
    private static readonly int[] CalibrationDuties = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };

    private readonly string _hwmonRoot;
    // Fans take a couple of seconds to reach a steady RPM after a pwm change;
    // calibration dwells at each step before sampling. Injectable for tests.
    private readonly TimeSpan _stepSettle;

    private readonly object _lock = new();
    private readonly Dictionary<string, FanPaths> _fanPaths = new();
    private readonly Dictionary<string, string> _tempInputPaths = new();
    private readonly HashSet<string> _warned = new();

    private readonly record struct FanPaths(string PwmPath, string EnablePath, string? FanInputPath);

    public LinuxFanControlProvider() : this("/sys/class/hwmon", TimeSpan.FromMilliseconds(2500)) { }

    internal LinuxFanControlProvider(string hwmonRoot, TimeSpan stepSettle)
    {
        _hwmonRoot = hwmonRoot;
        _stepSettle = stepSettle;
    }

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var discovered = EnumerateControllableFans().ToList();
        // Record the path map under the lock, but do the (potentially slow) sysfs
        // reads OUTSIDE it — a stuck hwmon read must not serialize every fan op.
        lock (_lock)
        {
            _fanPaths.Clear();
            foreach (var (id, _, paths) in discovered)
                _fanPaths[id] = paths;
        }

        var channels = new List<FanChannel>(discovered.Count);
        foreach (var (id, name, paths) in discovered)
        {
            var pwm = LinuxSysfs.ReadInt(paths.PwmPath) ?? 0;
            var rpm = paths.FanInputPath is not null ? LinuxSysfs.ReadInt(paths.FanInputPath) ?? 0 : 0;
            var enabled = LinuxSysfs.ReadInt(paths.EnablePath);
            channels.Add(new FanChannel
            {
                Id = id,
                Name = name,
                DutyPercent = (int)Math.Round(Math.Clamp(pwm, 0, 255) * 100.0 / 255.0),
                Rpm = rpm,
                Mode = enabled == 1 ? FanModes.Manual : FanModes.Auto,
            });
        }
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var discovered = EnumerateTemps().ToList();
        var sources = new List<TemperatureSource>(discovered.Count);
        lock (_lock)
        {
            _tempInputPaths.Clear();
            foreach (var (id, name, category, value, inputPath) in discovered)
            {
                _tempInputPaths[id] = inputPath;
                sources.Add(new TemperatureSource { Id = id, Name = name, Category = category, Value = value });
            }
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        string? path;
        lock (_lock)
        {
            if (!_tempInputPaths.TryGetValue(sensorId, out path))
                path = null;
        }
        if (path is null)
        {
            GetTemperatureSources();
            lock (_lock)
            {
                if (!_tempInputPaths.TryGetValue(sensorId, out path))
                    return null;
            }
        }
        var milli = LinuxSysfs.ReadInt(path);
        return milli is null ? null : milli.Value / 1000f;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        dutyPercent = Math.Clamp(dutyPercent, 0, 100);
        ApplyDuty(channelId, dutyPercent);
        return dutyPercent;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
        => ApplyDuty(channelId, Math.Clamp(dutyPercent, 0, 100));

    public void ReleaseFan(string channelId)
    {
        var paths = ResolveFan(channelId);
        if (paths is not null)
            ReleasePaths(paths.Value);
    }

    private static void ReleasePaths(FanPaths paths)
    {
        // Most SuperIO chips: 2 = automatic. A few only accept 0 (= no
        // software control / full speed) — fall back to that if 2 is rejected.
        if (!LinuxSysfs.WriteText(paths.EnablePath, "2"))
            LinuxSysfs.WriteText(paths.EnablePath, "0");
    }

    public void ReleaseAll()
    {
        GetFanChannels(); // ensure _fanPaths is populated
        List<string> ids;
        lock (_lock)
        {
            ids = _fanPaths.Keys.ToList();
        }
        foreach (var id in ids)
            ReleaseFan(id);
    }

    public async Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        GetFanChannels(); // populate _fanPaths
        List<(string Id, FanPaths Paths)> targets;
        lock (_lock)
        {
            var ids = (fanIds is null || fanIds.Count == 0)
                ? (IEnumerable<string>)_fanPaths.Keys
                : fanIds.Where(_fanPaths.ContainsKey);
            // Resolve every target's paths up front so a concurrent GetFanChannels /
            // ReleaseAll that clears _fanPaths can't make a fan unresolvable mid-ramp.
            targets = ids.Select(id => (id, _fanPaths[id])).ToList();
        }

        // Calibrate fans in parallel — each ramps its own pwm independently.
        var tasks = targets.Select(t => CalibrateOneAsync(t.Id, t.Paths, progress, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var channels = GetFanChannels();
        if (channels.Count == 0)
            return Array.Empty<CoolingComponent>();
        return new[]
        {
            new CoolingComponent
            {
                Id = "linux-fans",
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

    private async Task<FanCalibration?> CalibrateOneAsync(string id, FanPaths paths, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
    {
        var points = new List<FanCalibrationPoint>();
        try
        {
            LinuxSysfs.WriteText(paths.EnablePath, "1");
            for (var step = 0; step < CalibrationDuties.Length; step++)
            {
                ct.ThrowIfCancellationRequested();
                var duty = CalibrationDuties[step];
                LinuxSysfs.WriteText(paths.PwmPath, DutyToRaw(duty).ToString(CultureInfo.InvariantCulture));
                await Task.Delay(_stepSettle, ct).ConfigureAwait(false);
                var rpm = paths.FanInputPath is not null ? LinuxSysfs.ReadInt(paths.FanInputPath) ?? 0 : 0;
                points.Add(new FanCalibrationPoint { Duty = duty, Rpm = rpm });
                progress?.Report(new FanCalibrationProgress
                {
                    FanId = id,
                    CurrentDuty = duty,
                    CurrentRpm = rpm,
                    StepIndex = step,
                    TotalSteps = CalibrationDuties.Length,
                    State = "running",
                });
            }
        }
        finally
        {
            ReleasePaths(paths);
        }

        var spinning = points.Where(p => p.Rpm > 0).ToList();
        return new FanCalibration
        {
            FanId = id,
            Curve = points,
            MaxRpm = points.Count > 0 ? points.Max(p => p.Rpm) : 0,
            MinRpm = spinning.Count > 0 ? spinning.Min(p => p.Rpm) : 0,
            MinDuty = spinning.Count > 0 ? spinning.Min(p => p.Duty) : 0,
            CalibratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private void ApplyDuty(string channelId, int dutyPercent)
    {
        var paths = ResolveFan(channelId);
        if (paths is null)
            return;
        if (!LinuxSysfs.WriteText(paths.Value.EnablePath, "1") ||
            !LinuxSysfs.WriteText(paths.Value.PwmPath, DutyToRaw(dutyPercent).ToString(CultureInfo.InvariantCulture)))
            WarnOnce(channelId);
    }

    private FanPaths? ResolveFan(string id)
    {
        lock (_lock)
        {
            if (_fanPaths.TryGetValue(id, out var p))
                return p;
        }
        GetFanChannels(); // refresh — hwmonX numbering can change across reboots
        lock (_lock)
        {
            return _fanPaths.TryGetValue(id, out var p2) ? p2 : null;
        }
    }

    private IEnumerable<(string Id, string Name, FanPaths Paths)> EnumerateControllableFans()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(_hwmonRoot); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            var hwmonName = LinuxSysfs.ReadText(Path.Combine(dir, "name"));
            if (string.IsNullOrEmpty(hwmonName))
                continue;

            string[] pwmFiles;
            try { pwmFiles = Directory.GetFiles(dir, "pwm*"); }
            catch { continue; }

            foreach (var pwmPath in pwmFiles)
            {
                var n = BarePwmIndex(Path.GetFileName(pwmPath));
                if (n is null)
                    continue;
                var enablePath = Path.Combine(dir, $"pwm{n}_enable");
                if (!File.Exists(enablePath))
                    continue; // not software-controllable without an enable knob

                var fanInput = Path.Combine(dir, $"fan{n}_input");
                var label = LinuxSysfs.ReadText(Path.Combine(dir, $"fan{n}_label"));
                var name = !string.IsNullOrEmpty(label) ? $"{hwmonName} {label}" : $"{hwmonName} fan{n}";
                yield return (
                    $"linux/fan/{Sanitize(hwmonName)}/{n}",
                    name,
                    new FanPaths(pwmPath, enablePath, File.Exists(fanInput) ? fanInput : null));
            }
        }
    }

    private IEnumerable<(string Id, string Name, string Category, float Value, string InputPath)> EnumerateTemps()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(_hwmonRoot); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            var hwmonName = LinuxSysfs.ReadText(Path.Combine(dir, "name"));
            if (string.IsNullOrEmpty(hwmonName))
                continue;
            var category = CategoryFor(hwmonName);

            string[] inputs;
            try { inputs = Directory.GetFiles(dir, "temp*_input"); }
            catch { continue; }

            foreach (var input in inputs)
            {
                var file = Path.GetFileName(input);
                var index = file.Replace("temp", "").Replace("_input", "");
                var milli = LinuxSysfs.ReadInt(input);
                if (milli is null)
                    continue;
                var label = LinuxSysfs.ReadText(Path.Combine(dir, $"temp{index}_label"));
                var name = !string.IsNullOrEmpty(label) ? $"{hwmonName} {label}" : $"{hwmonName} temp{index}";
                yield return ($"linux/temp/{Sanitize(hwmonName)}/{index}", name, category, milli.Value / 1000f, input);
            }
        }
    }

    private static string CategoryFor(string hwmonName) => hwmonName switch
    {
        "k10temp" or "coretemp" or "zenpower" => "CPU",
        "amdgpu" or "nouveau" => "GPU",
        _ => "Motherboard",
    };

    private static int DutyToRaw(int dutyPercent) => (int)Math.Round(Math.Clamp(dutyPercent, 0, 100) * 255.0 / 100.0);

    /// <summary>Returns the index of a bare <c>pwmN</c> file, or null for <c>pwmN_enable</c>/<c>pwmN_mode</c>/etc.</summary>
    private static int? BarePwmIndex(string fileName)
    {
        if (!fileName.StartsWith("pwm", StringComparison.Ordinal))
            return null;
        var rest = fileName.Substring(3);
        return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private void WarnOnce(string channelId)
    {
        lock (_lock)
        {
            if (!_warned.Add(channelId))
                return;
        }
        Console.Error.WriteLine($"[cooling] fan write failed for {channelId} — check hwmon pwm permissions (udev rule / group). Reporting read-only.");
    }

    private static string Sanitize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
