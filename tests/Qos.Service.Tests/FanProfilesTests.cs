using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Cooling;
using Qos.Service.Models.Cooling;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

public class FanProfilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly TestableConfigStore _store;
    private readonly FakeFanProvider _fans;

    public FanProfilesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new TestableConfigStore(_settingsPath);
        _fans = new FakeFanProvider(
            channels: new List<FanChannel>
            {
                new() { Id = "fan1", Name = "Fan 1" },
                new() { Id = "fan2", Name = "Fan 2" },
            },
            temps: new List<TemperatureSource>
            {
                new() { Id = "cpu-package", Name = "CPU Package", Category = "CPU" },
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultActivePresetIsCustom()
    {
        var settings = _store.Load();
        Assert.Equal("custom", settings.Cooling.ActivePreset);
    }

    [Fact]
    public void ApplySilent_CreatesSinglePresetCurveAttachedToAllFans()
    {
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Equal("silent", s.Cooling.ActivePreset);
        var presetCurve = Assert.Single(s.Cooling.Curves, c => c.Preset == "silent");
        Assert.Equal("preset-silent", presetCurve.Id);
        Assert.Equal(2, presetCurve.Outputs.Count);
        Assert.Contains(presetCurve.Outputs, o => o.Id == "fan1");
        Assert.Contains(presetCurve.Outputs, o => o.Id == "fan2");
    }

    [Fact]
    public void ApplySilent_DoesNotDeleteUserCurves()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Name = "User A",
            Type = "Linear",
            Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
        }));

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Contains(s.Cooling.Curves, c => c.Id == "user-curve-a");
    }

    [Fact]
    public void ApplySilent_DetachesFansFromUserCurves()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Name = "User A",
            Type = "Linear",
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = "fan1", Type = "Fan" },
                new() { Id = "fan2", Type = "Fan" },
            },
        }));

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Empty(userCurve.Outputs);
    }

    [Fact]
    public void CustomToSilent_SnapshotsCustomMapping()
    {
        // Arrange a custom mapping: fan1 -> user-curve, fan2 -> BIOS (no curve).
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-curve-a",
                Name = "User A",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });

        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        Assert.Equal("user-curve-a", s.Cooling.CustomFanCurveAssignments["fan1"]);
        Assert.False(s.Cooling.CustomFanCurveAssignments.ContainsKey("fan2"));
    }

    [Fact]
    public void SilentToCustom_RestoresSnapshot()
    {
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-curve-a",
                Name = "User A",
                Type = "Linear",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });

        FanProfiles.Apply("silent", _fans, _store);
        FanProfiles.Apply("custom", _fans, _store);

        var s = _store.Load();
        Assert.Equal("custom", s.Cooling.ActivePreset);
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Single(userCurve.Outputs);
        Assert.Equal("fan1", userCurve.Outputs[0].Id);
        // Preset curve still exists but has no outputs.
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Empty(preset.Outputs);
    }

    [Fact]
    public void ApplyOff_DetachesAllFansAndReleases()
    {
        _store.Update(s => s.Cooling.Curves.Add(new CurveDocument
        {
            Id = "user-curve-a",
            Outputs = new List<CurveOutputDocument>
            {
                new() { Id = "fan1", Type = "Fan" },
                new() { Id = "fan2", Type = "Fan" },
            },
        }));

        FanProfiles.Apply("off", _fans, _store);

        var s = _store.Load();
        Assert.Equal("off", s.Cooling.ActivePreset);
        var userCurve = s.Cooling.Curves.First(c => c.Id == "user-curve-a");
        Assert.Empty(userCurve.Outputs);
        Assert.Equal(new[] { "fan1", "fan2" }.OrderBy(x => x), _fans.Released.OrderBy(x => x));
    }

    [Fact]
    public void EditingPresetCurve_SurvivesPresetRoundTrip()
    {
        FanProfiles.Apply("silent", _fans, _store);
        // User edits the preset curve's MinTemp.
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
            preset.Linear!.MinTemp = 50;
        });
        FanProfiles.Apply("custom", _fans, _store);
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        Assert.Equal(50, preset.Linear!.MinTemp);
    }

    [Fact]
    public void DeletedPresetCurve_RecreatedWithDefaults()
    {
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s => s.Cooling.Curves.RemoveAll(c => c.Preset == "silent"));
        FanProfiles.Apply("silent", _fans, _store);

        var s = _store.Load();
        var preset = s.Cooling.Curves.First(c => c.Preset == "silent");
        var defaults = FanProfiles.PresetDefaults.For("silent");
        Assert.Equal(defaults.MinTemp, preset.Linear!.MinTemp);
        Assert.Equal(defaults.MaxTemp, preset.Linear.MaxTemp);
        Assert.Equal(defaults.MinSpeed, preset.Linear.MinSpeed);
        Assert.Equal(defaults.MaxSpeed, preset.Linear.MaxSpeed);
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsActivePresetWhenCovering()
    {
        FanProfiles.Apply("balanced", _fans, _store);
        Assert.Equal("balanced", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsCustomWhenUserCurveDrivesAFan()
    {
        FanProfiles.Apply("balanced", _fans, _store);
        // User edits a non-preset curve to capture fan1.
        _store.Update(s =>
        {
            var preset = s.Cooling.Curves.First(c => c.Preset == "balanced");
            preset.Outputs.RemoveAll(o => o.Id == "fan1");
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-x",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ReturnsOffWhenNothingAttached()
    {
        FanProfiles.Apply("off", _fans, _store);
        Assert.Equal("off", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_ManualOnAnyFanIsCustom()
    {
        // Custom mode with one fan attached to a user curve and one fan on Manual:
        // the manual override means the configuration is mixed, not Off.
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.Curves.Add(new CurveDocument
            {
                Id = "user-a",
                Outputs = new List<CurveOutputDocument> { new() { Id = "fan1", Type = "Fan" } },
            });
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_AllManualIsCustomNotOff()
    {
        // Both fans on Manual -> custom (each fan is software-controlled by the
        // user even though no curve targets them).
        _store.Update(s =>
        {
            s.Cooling.ActivePreset = "custom";
            s.Cooling.ManualSpeeds["fan1"] = 60;
            s.Cooling.ManualSpeeds["fan2"] = 60;
        });
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void DerivePresetFromCurves_PresetWithManualOverrideIsCustom()
    {
        // All fans on preset-silent but one of them has a manual override:
        // the user broke out of the shared regime -> custom.
        FanProfiles.Apply("silent", _fans, _store);
        _store.Update(s => s.Cooling.ManualSpeeds["fan1"] = 50);
        Assert.Equal("custom", FanProfiles.DerivePresetFromCurves(_store, _fans));
    }

    [Fact]
    public void AutoIsTreatedAsOff()
    {
        FanProfiles.Apply("auto", _fans, _store);
        var s = _store.Load();
        Assert.Equal("off", s.Cooling.ActivePreset);
    }

    private sealed class FakeFanProvider : IFanControlProvider
    {
        private readonly List<FanChannel> _channels;
        private readonly List<TemperatureSource> _temps;
        public List<string> Released { get; } = new();

        public FakeFanProvider(List<FanChannel> channels, List<TemperatureSource> temps)
        {
            _channels = channels;
            _temps = temps;
        }

        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => _temps;
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => Math.Clamp(dutyPercent, 0, 100);
        public void ReleaseFan(string channelId) => Released.Add(channelId);
        public void ReleaseAll() => Released.AddRange(_channels.Select(c => c.Id));
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }
}
