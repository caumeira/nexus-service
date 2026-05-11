using System.Collections.Generic;

namespace Qos.Service.Peripherals.Hid;

/// <summary>
/// Enumerates HID devices matching a VID/PID filter and opens them for vendor-protocol
/// communication. Platform-specific (Windows uses SetupAPI + hid.dll).
/// </summary>
public interface IHidEnumerator
{
    /// <summary>Returns metadata for all HID interfaces matching (vendorId, productId). Does not open.</summary>
    IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId);

    /// <summary>Opens the device at the given path. Returns null if open fails.</summary>
    IHidDevice? Open(string path);
}

public sealed class HidDeviceInfo
{
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string Path { get; init; } = "";
    public string? Serial { get; init; }
    public int UsagePage { get; init; }
    public int Usage { get; init; }
    public int InputReportByteLength { get; init; }
    public int OutputReportByteLength { get; init; }
    public int FeatureReportByteLength { get; init; }
}
