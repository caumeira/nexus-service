using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Defaults;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Safety invariant for fan curves: a thermally loaded component must never be
/// allowed to sit at 0 RPM. Guards the shipped <c>install-defaults.json</c>
/// presets against a future edit that drops a floor to 0 (which would ship
/// silently — the existing FanProfilesTests assert plumbing, not duty math).
/// </summary>
public sealed class FanCurveSafetyTests
{
    public static IEnumerable<object[]> ShippedPresets()
        => InstallDefaults.Cooling.Presets.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(ShippedPresets))]
    public void Shipped_preset_never_allows_zero_duty(string preset)
    {
        var d = FanProfiles.PresetDefaults.For(preset);

        // The safety floor itself.
        Assert.True(d.MinSpeed > 0,
            $"{preset}: MinSpeed must be > 0 (got {d.MinSpeed}) — a 0 floor lets a hot part stall");
        Assert.True(d.MaxSpeed >= d.MinSpeed, $"{preset}: MaxSpeed must be >= MinSpeed");
        Assert.InRange(d.MaxSpeed, d.MinSpeed, 100);

        // And the Linear curve the engine builds from these defaults stays
        // positive across the whole temperature range, including a hot spike.
        var linear = new LinearCurveData
        {
            MinTemp = d.MinTemp,
            MaxTemp = d.MaxTemp,
            MinSpeed = d.MinSpeed,
            MaxSpeed = d.MaxSpeed,
        };
        Assert.True(CurveEngine.EvaluateLinear(linear, 95f) > 0, $"{preset}: duty at 95C must be > 0");
        Assert.True(CurveEngine.EvaluateLinear(linear, -10f) > 0, $"{preset}: floor below MinTemp must be > 0");
    }

    [Fact]
    public void Linear_curve_with_positive_floor_never_evaluates_below_it()
    {
        var linear = new LinearCurveData { MinTemp = 40, MaxTemp = 80, MinSpeed = 20, MaxSpeed = 100 };

        foreach (var t in new[] { -50f, 0f, 40f, 60f, 80f, 120f })
            Assert.True(CurveEngine.EvaluateLinear(linear, t) >= 20.0,
                $"duty at {t}C dropped below the configured floor");
    }
}
