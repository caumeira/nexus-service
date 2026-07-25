using System.Collections.Generic;

namespace Nexus.Service.Models.Peripherals;

// --- /peripherals/supported, /peripherals/all-supported - static catalogs ---

public sealed class SupportedDeviceDto
{
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();

    /// <summary>
    /// Which integration drives this device: "nexus" (first-party native driver)
    /// or "openrgb" (the bundled OpenRGB engine). The UI shows a per-row source icon.
    /// </summary>
    public string Source { get; set; } = "openrgb";
}

public sealed class GetSupportedDevicesResponse
{
    public List<SupportedDeviceDto> Items { get; set; } = new();
}
