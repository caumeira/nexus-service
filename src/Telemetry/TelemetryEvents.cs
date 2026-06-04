namespace Nexus.Service.Telemetry;

/// <summary>
/// Canonical event names. Add one line here and reference it at the call site —
/// never a raw string literal (keeps the taxonomy greppable and typo-proof).
/// snake_case to match PostHog conventions.
/// </summary>
public static class TelemetryEvents
{
    public const string AppStarted = "app_started";
    public const string DeviceConnected = "device_connected";
    public const string DeviceDisconnected = "device_disconnected";
    public const string FanSpeedSet = "fan_speed_set";
    public const string FanCurveApplied = "fan_curve_applied";
    public const string LightingEffectApplied = "lighting_effect_applied";
    public const string WidgetOpened = "widget_opened";
    public const string FirmwareFlashed = "firmware_flashed";
    public const string PairCompleted = "pair_completed";
}
