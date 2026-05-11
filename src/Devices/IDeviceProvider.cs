using System.Collections.Generic;
using Qos.Service.Models.Devices;

namespace Qos.Service.Devices;

public interface IDeviceProvider
{
    bool IsTypeConnected(string type);
    string GetFirmwareVersion(string type);
    GetCnvsSettingsResponse GetCnvs();
    GetCnvsSettingsResponse SetCnvs(SetCnvsSettingsBody body);
    bool CheckForUpdate(string id);
    (bool ok, string message) Update(string id);
    (bool ok, string message) UpdateProgress(string id);
    IReadOnlyList<MoboChannel> GetMotherboardLeds();
    IReadOnlyList<MoboChannel> SetMotherboardLeds(IReadOnlyList<SetChannel> channels);
    bool CheckFirmwareFunction(CheckFirmwareFunctionBody body);
}

public interface ILightingDeviceProvider
{
    bool IsConnected { get; }
    GetLightingDevicesResponse GetAll();
    void SetDisabled(IReadOnlyList<string> ids);
    void SetPower(string id, bool on);
    void SetBrightness(string id, int brightness);
    void SetHue(string id, float hue);
    void SetSaturation(string id, float saturation);
    /// <summary>Persist + apply a new LED count on a motherboard ARGB zone (id "openrgb-N-Z"). No-op for non-zone ids.</summary>
    void SetZoneLedCount(string id, int count);
    /// <summary>Pulse the target device (or single zone) with a unique colour for `durationMs` so the user can spot which physical strip is which.</summary>
    void Identify(string id, int durationMs);
}
