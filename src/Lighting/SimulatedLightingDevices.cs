#if DEV_TOOLS
using System;
using System.Collections.Generic;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Fake lighting cards appended by <see cref="CompositeLightingDeviceProvider"/>
/// when NEXUS_SIM_LIGHTING_DEVICES holds a positive count, so the onboarding
/// grid and lighting page can be exercised on a machine with no RGB hardware.
/// Ids are stable (sim-N) so ignore/power state persists across reloads; the
/// cards have no engine frame or transport, so effects and identify are inert.
/// </summary>
internal static class SimulatedLightingDevices
{
    private static readonly (string Name, int Leds)[] Catalog =
    {
        ("Sim ASUS ROG STRIX B650-F Aura", 3),
        ("Sim Corsair Vengeance RGB Pro", 10),
        ("Sim Corsair Vengeance RGB Pro #2", 10),
        ("Sim NVIDIA RTX 4080 SUPER", 12),
        ("Sim HYTE THICC FP12 Fan 1", 24),
        ("Sim HYTE THICC FP12 Fan 2", 24),
        ("Sim HYTE THICC FP12 Fan 3", 24),
        ("Sim HYTE LS30 LED Strip", 30),
        ("Sim Razer DeathAdder V3 Pro", 2),
        ("Sim Keychron Q1 Pro", 84),
        ("Sim Lian Li Strimer Plus V2", 108),
        ("Sim Kingston Fury Beast", 12),
    };

    public static readonly int Count = ReadCount();

    private static int ReadCount()
    {
        var raw = Environment.GetEnvironmentVariable("NEXUS_SIM_LIGHTING_DEVICES");
        return int.TryParse(raw, out var n) ? Math.Clamp(n, 0, 32) : 0;
    }

    public static List<LightingDevice> Build(NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var list = new List<LightingDevice>(Count);
        for (var i = 0; i < Count; i++)
        {
            var (name, leds) = Catalog[i % Catalog.Length];
            var id = $"sim-{i}";
            prefs.TryGetValue(id, out var pref);
            list.Add(new LightingDevice
            {
                Id = id,
                Name = i < Catalog.Length ? name : $"{name} ({i / Catalog.Length + 1})",
                LedsOn = !disabled.Contains(id),
                LedCount = leds,
                Brightness = pref?.Brightness ?? 100,
                Hue = pref?.Hue ?? 0,
                Saturation = pref?.Saturation ?? 1.0f,
            });
        }
        return list;
    }
}
#endif
