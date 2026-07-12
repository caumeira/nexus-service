using System.Collections.Generic;

namespace Nexus.Service.Models.Displays;

/// <summary>
/// One touch digitizer as Windows currently reports it: its raw-input device
/// interface path (GetRawInputDeviceInfoW RIDI_DEVICENAME - byte-identical to
/// the string Windows itself uses as the Digimon registry value name) and
/// which display id it is presently associated with.
/// </summary>
public sealed class TouchMapDigitizerInfo
{
    public string InterfacePath { get; set; } = "";
    public string ProductString { get; set; } = "";
    /// <summary>Display id currently associated by Windows; "" when unassociated.</summary>
    public string AssociatedDisplayId { get; set; } = "";
}

/// <summary>
/// One display, keyed the same way GET /displays/topology keys it, plus its
/// monitor device interface path (EnumDisplayDevicesW
/// EDD_GET_DEVICE_INTERFACE_NAME) - the exact string Windows expects as the
/// Digimon registry value data.
/// </summary>
public sealed class TouchMapDisplayInfo
{
    public string Id { get; set; } = "";
    public string MonitorInterfacePath { get; set; } = "";
}

/// <summary>Digitizer/display association snapshot for the touch-mapping guard.</summary>
public sealed class TouchMapSnapshot
{
    public List<TouchMapDigitizerInfo> Digitizers { get; set; } = new();
    public List<TouchMapDisplayInfo> Displays { get; set; } = new();
}

/// <summary>Response for POST /displays/touch-mapping/repair.</summary>
public sealed class TouchMappingRepairResponse
{
    /// <summary>"repaired" | "alreadyCorrect" | "noPanel" | "noDigitizer" | "noHelper" | "failed".</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}
