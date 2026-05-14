namespace Qos.Service.Models.Widgets;

/// <summary>
/// JSON-RPC 2.0 error codes used by the host-to-widget bridge.
/// Standard codes (-32600..-32603) follow the spec; Qos-specific codes
/// live in the application-error range (-32000..-32099).
/// </summary>
public static class WidgetErrorCodes
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int CapabilityDenied = -32000;
    public const int InternalError = -32001;
}
