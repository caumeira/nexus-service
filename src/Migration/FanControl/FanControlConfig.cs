using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Nexus.Service.Migration.FanControl;

/// <summary>Curve kinds FanControl serializes. Distinguished by which keys a curve object carries; there is no type tag in the file.</summary>
internal static class FanControlCurveKinds
{
    public const string Flat = "flat";
    public const string Graph = "graph";
    public const string Linear = "linear";
    public const string Mix = "mix";
    public const string Trigger = "trigger";
    public const string Sync = "sync";
    public const string Auto = "auto";
    public const string Unknown = "unknown";
}

/// <summary>One entry of a FanControl fan curve. Only the fields for the detected <see cref="Kind"/> are populated.</summary>
internal sealed class FanControlCurve
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = FanControlCurveKinds.Unknown;
    public bool IsHidden { get; set; }
    /// <summary>0 = percent, 1 = RPM. Nexus curves are percent-only, so RPM curves are reported and skipped.</summary>
    public int CommandMode { get; set; }

    public string? TempSourceIdentifier { get; set; }
    public double ResponseTime { get; set; } = 1;

    public double Percent { get; set; }
    public List<(double Temp, double Speed)> Points { get; } = new();
    /// <summary>Points present in the file that could not be read. Any at all makes the curve a different curve, so it is not imported.</summary>
    public int UnreadablePoints { get; set; }
    public double MinimumTemperature { get; set; }
    public double MaximumTemperature { get; set; }
    public double MinimumFanSpeed { get; set; }
    public double MaximumFanSpeed { get; set; }

    public List<string> MixCurveNames { get; } = new();
    public int MixFunction { get; set; }

    public double IdleTemperature { get; set; }
    public double LoadTemperature { get; set; }
    public double IdleFanSpeed { get; set; }
    public double LoadFanSpeed { get; set; }

    public string? SyncControlIdentifier { get; set; }
    public double SyncOffset { get; set; }
    public bool SyncProportional { get; set; }

    public double Step { get; set; } = 5;
    public double Deadband { get; set; } = 2;
}

/// <summary>One FanControl control (a fan channel it can drive).</summary>
internal sealed class FanControlControl
{
    public string Identifier { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NickName { get; set; }
    public bool Enable { get; set; }
    /// <summary>Name of the curve bound to this control; null when it is on manual or unbound.</summary>
    public string? SelectedFanCurveName { get; set; }
    public double SelectedOffset { get; set; }
    public int SelectedStart { get; set; }
    public int SelectedStop { get; set; }
    public int MinimumPercent { get; set; }
    public bool ManualControl { get; set; }
    public double ManualControlValue { get; set; }
    /// <summary>Duty-to-RPM steps, as measured by FanControl's own calibration.</summary>
    public List<(int Percent, int Rpm)> Calibration { get; } = new();
}

/// <summary>A parsed FanControl configuration file.</summary>
internal sealed class FanControlConfig
{
    public int Version { get; set; }
    public List<FanControlCurve> Curves { get; } = new();
    public List<FanControlControl> Controls { get; } = new();
}

/// <summary>
/// Reads a FanControl <c>userConfig.json</c>. Hand-rolled over JsonDocument
/// rather than deserialized: the file has no type discriminator (a curve's kind
/// is inferred from which keys it carries), it carries keys we do not model,
/// and a single unreadable curve must not lose the rest of the file.
/// </summary>
internal static class FanControlConfigParser
{
    public static FanControlConfig Parse(string json)
    {
        var config = new FanControlConfig();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return config;
        }

        // Written as a JSON string in shipping configs, so this cannot use TryGetInt32.
        config.Version = (int)Num(root, "__VERSION__");

        if (!root.TryGetProperty("Main", out var main) || main.ValueKind != JsonValueKind.Object)
        {
            return config;
        }

        if (main.TryGetProperty("FanCurves", out var curves) && curves.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in curves.EnumerateArray())
            {
                var curve = ParseCurve(element);
                if (curve is not null)
                {
                    config.Curves.Add(curve);
                }
            }
        }

        if (main.TryGetProperty("Controls", out var controls) && controls.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in controls.EnumerateArray())
            {
                var control = ParseControl(element);
                if (control is not null)
                {
                    config.Controls.Add(control);
                }
            }
        }

        return config;
    }

    private static FanControlCurve? ParseCurve(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var curve = new FanControlCurve
        {
            Name = Str(e, "Name") ?? "",
            IsHidden = Bool(e, "IsHidden"),
            CommandMode = (int)Num(e, "CommandMode"),
            TempSourceIdentifier = Identifier(e, "SelectedTempSource"),
            ResponseTime = Num(e, "SelectedResponseTime", 1),
            MinimumTemperature = Num(e, "MinimumTemperature"),
            MaximumTemperature = Num(e, "MaximumTemperature"),
            // Linear spells these Minimum/MaximumFanSpeed; Auto spells the same
            // pair Min/MaxFanSpeed.
            MinimumFanSpeed = Num(e, "MinimumFanSpeed", Num(e, "MinFanSpeed")),
            MaximumFanSpeed = Num(e, "MaximumFanSpeed", Num(e, "MaxFanSpeed")),
            IdleTemperature = Num(e, "IdleTemperature"),
            LoadTemperature = Num(e, "LoadTemperature"),
            IdleFanSpeed = Num(e, "IdleFanSpeed"),
            LoadFanSpeed = Num(e, "LoadFanSpeed"),
            Percent = Num(e, "Percent"),
            Step = Num(e, "Step", 5),
            Deadband = Num(e, "Deadband", 2),
            SyncControlIdentifier = Identifier(e, "SelectedControl"),
            SyncOffset = Num(e, "SelectedOffset"),
            SyncProportional = Bool(e, "Proportional"),
            MixFunction = (int)Num(e, "SelectedMixFunction"),
        };

        if (e.TryGetProperty("Points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in points.EnumerateArray())
            {
                if (TryParsePoint(p, out var point))
                {
                    curve.Points.Add(point);
                }
                else
                {
                    curve.UnreadablePoints++;
                }
            }
        }

        if (e.TryGetProperty("SelectedFanCurves", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in members.EnumerateArray())
            {
                var name = m.ValueKind == JsonValueKind.String ? m.GetString() : Str(m, "Name");
                if (!string.IsNullOrEmpty(name))
                {
                    curve.MixCurveNames.Add(name);
                }
            }
        }

        curve.Kind = DetectKind(e);
        return curve;
    }

    /// <summary>
    /// Key-set discrimination, most specific first: Graph and Mix own a
    /// collection each, Auto is the only kind with Step + Deadband, Trigger the
    /// only other one with idle/load speeds, Sync the only one bound to a
    /// control, Linear the only one with Minimum/MaximumFanSpeed.
    /// </summary>
    internal static string DetectKind(JsonElement e)
    {
        if (Has(e, "Points")) return FanControlCurveKinds.Graph;
        if (Has(e, "SelectedFanCurves")) return FanControlCurveKinds.Mix;
        if (Has(e, "Step") && Has(e, "Deadband")) return FanControlCurveKinds.Auto;
        if (Has(e, "IdleFanSpeed") || Has(e, "LoadFanSpeed")) return FanControlCurveKinds.Trigger;
        if (Has(e, "SelectedControl")) return FanControlCurveKinds.Sync;
        if (Has(e, "MinimumFanSpeed") || Has(e, "MaximumFanSpeed")) return FanControlCurveKinds.Linear;
        if (Has(e, "MinFanSpeed") || Has(e, "MaxFanSpeed")) return FanControlCurveKinds.Auto;
        if (Has(e, "Percent")) return FanControlCurveKinds.Flat;
        return FanControlCurveKinds.Unknown;
    }

    private static FanControlControl? ParseControl(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var identifier = Str(e, "Identifier");
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        var control = new FanControlControl
        {
            Identifier = identifier,
            Name = Str(e, "Name") ?? "",
            NickName = Str(e, "NickName"),
            Enable = Bool(e, "Enable"),
            SelectedFanCurveName = Str(e, "SelectedFanCurve") ?? Identifier(e, "SelectedFanCurve", "Name"),
            SelectedOffset = Num(e, "SelectedOffset"),
            SelectedStart = (int)Num(e, "SelectedStart"),
            SelectedStop = (int)Num(e, "SelectedStop"),
            MinimumPercent = (int)Num(e, "MinimumPercent"),
            ManualControl = Bool(e, "ManualControl"),
            ManualControlValue = Num(e, "ManualControlValue"),
        };

        if (e.TryGetProperty("Calibration", out var cal) && cal.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in cal.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var pair = new List<double>(2);
                foreach (var cell in row.EnumerateArray())
                {
                    if (TryNumber(cell, out var value))
                    {
                        pair.Add(value);
                    }
                }
                if (pair.Count >= 2)
                {
                    control.Calibration.Add(((int)Math.Round(pair[0]), (int)Math.Round(pair[1])));
                }
            }
        }

        return control;
    }

    /// <summary>A graph point: FanControl writes "temp,percent" strings, older files a 2-element array.</summary>
    internal static bool TryParsePoint(JsonElement e, out (double Temp, double Speed) point)
    {
        point = default;
        if (e.ValueKind == JsonValueKind.String)
        {
            var raw = e.GetString();
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            var parts = raw.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                point = (t, s);
                return true;
            }
            return false;
        }

        if (e.ValueKind == JsonValueKind.Array)
        {
            var values = new List<double>(2);
            foreach (var cell in e.EnumerateArray())
            {
                if (TryNumber(cell, out var v))
                {
                    values.Add(v);
                }
            }
            if (values.Count >= 2)
            {
                point = (values[0], values[1]);
                return true;
            }
        }

        return false;
    }

    private static bool Has(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && TryNumber(v, out var value) ? value : fallback;

    private static bool TryNumber(JsonElement e, out double value)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            case JsonValueKind.True:
                value = 1;
                return true;
            case JsonValueKind.False:
                value = 0;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Reads a nested object's field (defaults to Identifier), e.g. SelectedTempSource.Identifier.</summary>
    private static string? Identifier(JsonElement e, string name, string field = "Identifier") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? Str(v, field) : null;
}
