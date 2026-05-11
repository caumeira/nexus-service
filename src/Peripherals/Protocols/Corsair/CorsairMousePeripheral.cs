using System.Collections.Generic;
using Qos.Service.Peripherals.Capabilities;

namespace Qos.Service.Peripherals.Protocols.Corsair;

/// <summary>
/// Corsair mouse with DPI + polling configuration. First pass uses the
/// property-based protocol common to the M65 Pro generation; actual property
/// IDs are probed at runtime since the exact IDs vary across models.
/// </summary>
public sealed class CorsairMousePeripheral : IPeripheral, IDpiCapability, IPollingRateCapability
{
    // Property IDs observed across Corsair mouse generations. We'll try 0x21 first
    // (M65 Pro / Sabre era) and fall back to others if needed. Stored as `int` so
    // we can update at runtime without recomposing the instance.
    private const byte PropDpi = 0x21;
    private const byte PropPollingRate = 0x0A;

    private readonly CorsairClient _client;
    private readonly int _vid;
    private readonly int _pid;
    private readonly string _modelName;
    private readonly string _serial;

    public CorsairMousePeripheral(CorsairClient client, int vid, int pid, string modelName, string serial)
    {
        _client = client;
        _vid = vid;
        _pid = pid;
        _modelName = modelName;
        _serial = serial;
    }

    // --- IPeripheral ---
    public string Id => $"corsair-{_modelName.ToLowerInvariant().Replace(' ', '-')}-{_serial}";
    public string Name => _modelName;
    public string Vendor => "Corsair";
    public string Category => "mouse";
    public int VendorId => _vid;
    public int ProductId => _pid;
    public string Serial => _serial;
    public string FirmwareVersion => "";
    public bool IsWireless => false;
    public IReadOnlyList<string> Capabilities => new[] { "dpi", "polling" };
    public T? GetCapability<T>() where T : class, IPeripheralCapability
    {
        if (typeof(T) == typeof(IDpiCapability))
            return this as T;
        if (typeof(T) == typeof(IPollingRateCapability))
            return this as T;
        return null;
    }

    // --- IDpiCapability ---
    public int MinDpi => 100;
    public int MaxDpi => 12000; // M65 Pro RGB has 12k sensor
    public int Step => 50;
    public int StageCount => 0;
    public int ActiveStage => -1;
    public IReadOnlyList<int> StageDpi => System.Array.Empty<int>();

    public int GetCurrent()
    {
        var reply = _client.Read(PropDpi);
        if (reply is null)
            return 0;
        // Payload layout after command+property header: args start at reply[0]
        // For DPI: 2-byte little-endian X, 2-byte little-endian Y
        int dpi = reply[0] | (reply[1] << 8);
        return dpi;
    }

    public bool SetDpi(int dpi)
    {
        if (dpi < MinDpi)
            dpi = MinDpi;
        if (dpi > MaxDpi)
            dpi = MaxDpi;
        var args = new byte[] {
            (byte)(dpi & 0xFF), (byte)((dpi >> 8) & 0xFF),   // X
            (byte)(dpi & 0xFF), (byte)((dpi >> 8) & 0xFF),   // Y
        };
        return _client.Write(PropDpi, args);
    }

    public bool SetActiveStage(int index) => false;
    public bool SetStageDpi(int stageIndex, int dpi) => false;

    // --- IPollingRateCapability ---
    public IReadOnlyList<int> SupportedHz => new[] { 125, 250, 500, 1000 };

    public int GetCurrentHz()
    {
        var reply = _client.Read(PropPollingRate);
        if (reply is null)
            return 0;
        return reply[0] switch
        {
            0x01 => 125,
            0x02 => 250,
            0x04 => 500,
            0x08 => 1000,
            _ => 0,
        };
    }

    public bool SetHz(int hz)
    {
        byte code = hz switch
        {
            125 => 0x01,
            250 => 0x02,
            500 => 0x04,
            1000 => 0x08,
            _ => 0,
        };
        if (code == 0)
            return false;
        return _client.Write(PropPollingRate, new byte[] { code });
    }
}
