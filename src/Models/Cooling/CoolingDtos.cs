using System.Collections.Generic;

namespace Qos.Service.Models.Cooling;

// ----- Top-level cooling component shape (for /cooling/all) -----

public class CoolingComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of: Q60, Q80, P60, P80, NP50, MiniHub, Motherboard, GPU, PwmFanAndArgbHub.</summary>
    public string Type { get; set; } = "MiniHub";
    public List<CoolingDevice> Devices { get; set; } = new();
}

public class CoolingDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Fan";
    public int? Speed { get; set; }
    public int? Rpm { get; set; }
    public int? TargetRpm { get; set; }
    public float? Temperature { get; set; }
    public float? PumpTempIn { get; set; }
    public float? PumpTempOut { get; set; }
    public int? Pwm { get; set; }
}

public class GetAllCoolingResponse : ApiResponse
{
    public List<CoolingComponent> CoolingComponents { get; set; } = new();
}

// ----- /cooling/curves/set -----

public class SetCurvesBody
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<Curve> Curves { get; set; } = new();
}

public class Curve
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of: Flat, Linear, Graph, Mixed.</summary>
    public string Type { get; set; } = "Flat";
    public CurveInput Input { get; set; } = new();
    public List<CurveOutput> Outputs { get; set; } = new();
    public FlatCurve? Flat { get; set; }
    public LinearCurve? Linear { get; set; }
    public GraphCurve? Graph { get; set; }
    public MixedCurve? Mixed { get; set; }
    /// <summary>"silent" | "balanced" | "performance" for the shared preset curves; null for user curves. Independent of Type.</summary>
    public string? Preset { get; set; }
    /// <summary>For preset curves only: true when the curve's Type + Linear params match <see cref="Qos.Service.Cooling.FanProfiles.PresetDefaults"/>. Null for user curves. Drives the Reset-to-defaults button's enabled state in the SPA, so the FE doesn't have to mirror PresetDefaults locally.</summary>
    public bool? IsDefault { get; set; }
}

public class CurveInput
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Device { get; set; } = "";
}

public class CurveOutput
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
}

public class FlatCurve { public int Speed { get; set; } }
public class MixedCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public List<string> CurveIds { get; set; } = new();
    public string Fn { get; set; } = "max";
}

public class LinearCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public double MinTemp { get; set; }
    public double MaxTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
}

public class GraphCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public double SpeedModifier { get; set; } = 1.0;
    public List<GraphPoint> Points { get; set; } = new();
}

public class GraphPoint
{
    public double Temp { get; set; }
    public double Speed { get; set; }
}

// ----- Fan control (IFanControlProvider) -----

/// <summary>A controllable fan channel discovered from hardware.</summary>
public sealed class FanChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DutyPercent { get; set; }
    public int Rpm { get; set; }
    /// <summary>"Auto" | "Manual" | "Curve"</summary>
    public string Mode { get; set; } = "Auto";
    public int? MinRpm { get; set; }
    public int? MaxRpm { get; set; }
    public int? MinDuty { get; set; }
    public string? Classification { get; set; }
    public bool Calibrated => MinRpm is not null;
}

/// <summary>A temperature sensor available as curve input.</summary>
public sealed class TemperatureSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"CPU", "GPU", "Motherboard", "Storage"</summary>
    public string Category { get; set; } = "";
    public float Value { get; set; }
}

public sealed class GetCurvesResponse : ApiResponse
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<Curve> Curves { get; set; } = new();
}

public sealed class GetFanChannelsResponse : ApiResponse
{
    public List<FanChannel> Channels { get; set; } = new();
}

public sealed class GetTemperatureSourcesResponse : ApiResponse
{
    public List<TemperatureSource> Sources { get; set; } = new();
}

public sealed class SetFanSpeedBody
{
    public int Speed { get; set; }
}

public sealed class SetFanSpeedResponse : ApiResponse
{
    public string ChannelId { get; set; } = "";
    public int Speed { get; set; }
    public string Mode { get; set; } = "Manual";
}

public sealed class SetFanNameBody
{
    public string Name { get; set; } = "";
}

// ----- Curve engine WebSocket push -----

public sealed class CurveOutputState
{
    public string ChannelId { get; set; } = "";
    public int AppliedSpeed { get; set; }
}

public sealed class CurveCalculation
{
    public string CurveId { get; set; } = "";
    public string InputSensorId { get; set; } = "";
    public float InputTemperature { get; set; }
    public double CalculatedSpeed { get; set; }
    public double ActualSpeed { get; set; }
    public List<CurveOutputState> Outputs { get; set; } = new();
}

public sealed class CurveCalculationsFrame
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<CurveCalculation> Calculations { get; set; } = new();
}

// ----- Fan profiles -----

public sealed class FanProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class GetProfilesResponse : ApiResponse
{
    public List<FanProfile> Profiles { get; set; } = new();
    public string Active { get; set; } = "";
}

public sealed class ApplyProfileResponse : ApiResponse
{
    public string Applied { get; set; } = "";
}
