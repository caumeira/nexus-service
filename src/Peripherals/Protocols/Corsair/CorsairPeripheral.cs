using System.Collections.Generic;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Capabilities;

namespace Nexus.Service.Peripherals.Protocols.Corsair;

/// <summary>
/// Corsair mouse detection shell. Recognizes the device identity and surfaces
/// it in the UI; exposes no capability controls (Corsair's iCUE vendor protocol
/// isn't reverse-engineered to the level Razer / HID++ are).
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
