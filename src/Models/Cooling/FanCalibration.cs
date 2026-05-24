using System;
using System.Collections.Generic;

namespace Nexus.Service.Models.Cooling;

public sealed class FanCalibration
{
    public string FanId { get; set; } = "";
    public string Classification { get; set; } = "";
    public int MinRpm { get; set; }
    public int MaxRpm { get; set; }
    public int MinDuty { get; set; }
    public List<FanCalibrationPoint> Curve { get; set; } = new();
    public long CalibratedAtUnixMs { get; set; }
}

public sealed class FanCalibrationPoint
{
    public int Duty { get; set; }
    public int Rpm { get; set; }
}

public sealed class FanCalibrationProgress
{
    public string FanId { get; set; } = "";
    public int CurrentDuty { get; set; }
    public int CurrentRpm { get; set; }
    public int StepIndex { get; set; }
    public int TotalSteps { get; set; } = 11;
    public string State { get; set; } = "";
}

public sealed class StartCalibrationBody
{
    public List<string> FanIds { get; set; } = new();
}

public sealed class CalibrationStartResponse
{
    public string SessionId { get; set; } = "";
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

public sealed class CoolingStatusResponse
{
    public bool Calibrating { get; set; }
    public string CalibrationState { get; set; } = "idle";
    public int ActiveCurves { get; set; }
    public int FanCount { get; set; }
    /// <summary>Fans whose speed is driven by a curve. Drives the sidebar status dot.</summary>
    public int ActiveCurveFanCount { get; set; }
    /// <summary>Fans in user-set bias mode (software-controlled with no curve bound).</summary>
    public int ManualFans { get; set; }
}

public sealed class LightingStatusResponse
{
    public string Effect { get; set; } = "none";
    public bool Running { get; set; }
    public bool Scanning { get; set; }
    public bool RgbRunning { get; set; }
    public bool GpuAvailable { get; set; }
}

public sealed class GetCalibrationsResponse
{
    public List<FanCalibration> Calibrations { get; set; } = new();
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}
