using System;
using System.Collections.Generic;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

public sealed class StubDeviceProvider : IDeviceProvider, ILightingDeviceProvider
{
    private readonly IConfigStore _store;

    public StubDeviceProvider(IConfigStore store) { _store = store; }

    // ----- IDeviceProvider -----
    public bool IsTypeConnected(string type) => false;
    public string GetFirmwareVersion(string type) => "";

    public GetCnvsSettingsResponse GetCnvs()
    {
        var s = _store.Load().Devices.Cnvs;
        return new() { PlayAnimation = s.PlayAnimation, PlayWhenPCOff = s.PlayWhenPCOff };
    }

    public GetCnvsSettingsResponse SetCnvs(SetCnvsSettingsBody body)
    {
        _store.Update(s =>
        {
            s.Devices.Cnvs.PlayAnimation = body.PlayAnimation;
            s.Devices.Cnvs.PlayWhenPCOff = body.PlayWhenPCOff;
        });
        return new() { PlayAnimation = body.PlayAnimation, PlayWhenPCOff = body.PlayWhenPCOff };
    }

    public bool CheckForUpdate(string id) => false;
    public (bool ok, string message) Update(string id) => (false, "no device");
    public (bool ok, string message) UpdateProgress(string id) => (false, "0");

    public IReadOnlyList<MoboChannel> GetMotherboardLeds()
    {
        var s = _store.Load().Devices.MotherboardLeds;
        var list = new List<MoboChannel>();
        foreach (var ch in s)
        {
            list.Add(new MoboChannel { Name = ch.Name, Count = ch.Count, Max = 256 });
        }

        return list;
    }

    public IReadOnlyList<MoboChannel> SetMotherboardLeds(IReadOnlyList<SetChannel> channels)
    {
        _store.Update(s =>
        {
            s.Devices.MotherboardLeds.Clear();
            foreach (var ch in channels)
            {
                s.Devices.MotherboardLeds.Add(new MotherboardLedChannel { Name = ch.Name, Count = ch.Count });
            }
        });
        return GetMotherboardLeds();
    }

    public bool CheckFirmwareFunction(CheckFirmwareFunctionBody body) => false;

    // ----- ILightingDeviceProvider -----
    public bool IsConnected => false;

    public GetLightingDevicesResponse GetAll() => new()
    {
        IsInit = false,
        Devices = new List<LightingDevice>(),
    };

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id))
                return;
            var next = new List<string>(current.Count);
            foreach (var x in current)
            { if (x != id) next.Add(x); }
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id))
                return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = brightness;
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count) => _store.Update(s =>
    {
        if (string.IsNullOrEmpty(id) || count < 0)
            return;
        s.Devices.ZoneLedCounts[id] = count;
    });

    public void Identify(string id, int durationMs) { }
}
