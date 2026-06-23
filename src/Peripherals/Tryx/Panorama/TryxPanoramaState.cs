namespace Nexus.Service.Peripherals.Tryx.Panorama;

public sealed class TryxPanoramaState
{
    public string Serial { get; set; } = "";
    public string AdbSerial { get; set; } = "";
    public string PortName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int ProductId { get; set; }
    public bool ScreenEnabled { get; set; } = true;
    public int Brightness { get; set; } = 100;
    /// <summary>Bare filename on the device under /sdcard/pcMedia/, or a preset id.</summary>
    public string CurrentMedia { get; set; } = "";
    public bool CurrentMediaIsCustom { get; set; }
    public long LastConnectedMs { get; set; }
    public long LastFrameMs { get; set; }
}
