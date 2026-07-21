namespace Nexus.Service.Monitoring.Events;

/// <summary>One monitoring timeline event. TUtcMs is the wire's UTC epoch
/// milliseconds, the same base as /monitoring/history. Custom is true only
/// for user-created events (POST /monitoring/events) - those are the only
/// ones DeleteCustom accepts.</summary>
public sealed record MonitoringEvent(long Id, long TUtcMs, string Kind, string Label, string? Detail, bool Custom);

/// <summary>Kind constants for MonitoringEvent.Kind, pinned to the frozen
/// nexus-web wire contract - do not rename or add without updating the web
/// side.</summary>
public static class MonitoringEventKinds
{
    public const string AppOpen = "app-open";
    public const string UacEscalation = "uac-escalation";
    public const string UsbAttach = "usb-attach";
    public const string UsbDetach = "usb-detach";
    public const string Custom = "custom";
}
