using System.Collections.Generic;
using Qos.Service.Peripherals;
using Qos.Service.Peripherals.Capabilities;

namespace Qos.Service.Peripherals.Protocols.Corsair;

/// <summary>
/// Corsair mouse detection shell. Corsair's vendor HID protocol (iCUE) isn't
/// reverse-engineered to the level Razer / HID++ are in the open-source world,
/// so for v1 we recognize the device identity and surface it in the UI but
/// don't expose live capability controls yet. Configuration is "planned" —
/// the user sees that qOS knows about their Corsair peripheral.
/// </summary>
public sealed class CorsairPeripheral : IPeripheral
{
    private readonly int _vid;
    private readonly int _pid;
    private readonly string _modelName;
    private readonly string _category;

    public CorsairPeripheral(int vid, int pid, string modelName, string category, string serial)
    {
        _vid = vid;
        _pid = pid;
        _modelName = modelName;
        _category = category;
        Serial = serial;
    }

    public string Id => $"corsair-{_modelName.ToLowerInvariant().Replace(' ', '-')}-{Serial}";
    public string Name => _modelName;
    public string Vendor => "Corsair";
    public string Category => _category;
    public int VendorId => _vid;
    public int ProductId => _pid;
    public string Serial { get; }
    public string FirmwareVersion => "";
    public bool IsWireless => false;
    public IReadOnlyList<string> Capabilities => System.Array.Empty<string>();
    public T? GetCapability<T>() where T : class, IPeripheralCapability => null;
}
