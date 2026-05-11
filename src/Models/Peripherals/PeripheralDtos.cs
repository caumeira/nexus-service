using System.Collections.Generic;

namespace Qos.Service.Models.Peripherals;

/// <summary>Item in /peripherals list — live, detected peripherals with capabilities.</summary>
public sealed class PeripheralDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Category { get; set; } = "";
    public string VendorId { get; set; } = "";   // "0x1532"
    public string ProductId { get; set; } = "";  // "0x007C"
    public string Serial { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public bool IsWireless { get; set; }
    public List<string> Capabilities { get; set; } = new();

    // Populated for GET /peripherals/{id}
    public DpiState? Dpi { get; set; }
    public PollingState? Polling { get; set; }
    public BatteryState? Battery { get; set; }
    public SleepState? Sleep { get; set; }
    public List<ToggleState> Toggles { get; set; } = new();
}

public sealed class DpiState
{
    public int MinDpi { get; set; }
    public int MaxDpi { get; set; }
    public int Step { get; set; }
    public int StageCount { get; set; }
    public int ActiveStage { get; set; }
    public List<int> StageDpi { get; set; } = new();
    public int Current { get; set; }
}

public sealed class PollingState
{
    public List<int> SupportedHz { get; set; } = new();
    public int CurrentHz { get; set; }
}

public sealed class BatteryState
{
    public int Percent { get; set; }
    public bool Charging { get; set; }
}

public sealed class SleepState
{
    public int IdleSeconds { get; set; }
    public int LowBatteryPercent { get; set; }
}

public sealed class ToggleState
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
}

// --- request bodies ---

public sealed class SetDpiBody { public int? Dpi { get; set; } public int? Stage { get; set; } }
public sealed class SetPollingBody { public int Hz { get; set; } }
public sealed class SetSleepBody { public int? IdleSeconds { get; set; } public int? LowBatteryPercent { get; set; } }
public sealed class SetToggleBody { public string Key { get; set; } = ""; public bool Enabled { get; set; } }

// --- /peripherals/supported — static catalog ---

public sealed class SupportedDeviceDto
{
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
}

public sealed class GetPeripheralsResponse
{
    public List<PeripheralDto> Items { get; set; } = new();
}

public sealed class GetSupportedDevicesResponse
{
    public List<SupportedDeviceDto> Items { get; set; } = new();
}

