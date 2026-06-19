using System;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pushes the persisted keeb desired-state (game mode + firmware animation +
/// rotary) to the device as a single 0x06 settings write. Shared by:
///   - the REST provider, after any settings change,
///   - the connection worker, when the keeb first appears (so saved settings
///     take effect on plug-in),
///   - the lighting frame writer, when software streaming stops (re-asserts the
///     firmware animation, which streaming had suppressed).
/// Desired-state model: we always write the COMPLETE state from settings.json.
/// The one exception is device-initiated changes the host can't otherwise see -
/// the firmware-mode rotary cycles the effect and moves the brightness byte
/// without a host callback - which <see cref="SyncFromDevice"/> reads back so
/// persisted state (and the software stream) follows the hardware.
/// </summary>
public sealed class KeebSettingsApplier
{
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _panel;

    public KeebSettingsApplier(KeebHub hub, IConfigStore store, MultiplexHub panel)
    {
        _hub = hub;
        _store = store;
        _panel = panel;
    }

    /// <summary>Build the page from current settings and write it. No-op (false) when disconnected.</summary>
    public bool Apply()
    {
        if (!_hub.IsConnected) return false;
        try
        {
            var page = KeebSettingsCodec.BuildSettingsPage(_store.Load().Keeb);
            return _hub.WriteSettings(page);
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] apply settings failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Mirror a 0-100 brightness onto the firmware-lighting setting and both keeb
    /// zone prefs. The firmware byte dims only the firmware animation, so the zone
    /// prefs carry the same value into the software stream's per-zone scaling -
    /// brightness then dims the keyboard identically in either mode. Shared by the
    /// REST setter and the device read-back so the two paths can't drift.
    /// </summary>
    public static void ApplyBrightnessToSettings(NexusSettings s, string? hubId, int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        s.Keeb.FirmwareLighting.Brightness = brightness;
        if (string.IsNullOrEmpty(hubId)) return;
        foreach (var id in new[]
        {
            hubId + KeebLightingDeviceProvider.KeysSuffix,
            hubId + KeebLightingDeviceProvider.UnderglowSuffix,
        })
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            {
                pref = new LightingDevicePreference();
                s.Devices.LightingDevicePrefs[id] = pref;
            }
            pref.Brightness = brightness;
        }
    }

    /// <summary>
    /// Read the device's current firmware effect and brightness and mirror a
    /// device-initiated change into persisted state, so the panel and the software
    /// stream follow the hardware. In firmware rotary mode the middle button
    /// cycles the effect and the knob moves the brightness byte with NO host
    /// callback, so the connection worker calls this each poll tick. Our own writes
    /// keep device + persisted in sync, so only a hardware-side change produces a
    /// mismatch. Broadcasts lighting and returns true when it updated anything.
    /// Read-only toward the device: it must never Apply()/WriteSettings. Both the
    /// no-oscillation equality check and the non-atomic read-then-update (this runs
    /// from the poll thread and the input-worker middle-click) stay correct only
    /// because nothing in this path writes the device.
    /// </summary>
    public bool SyncFromDevice()
    {
        if (!_hub.IsConnected) return false;
        var raw = _hub.ReadSettings();
        if (raw is null) return false;
        // Read layout (verified on the bench): Linux hidraw returns the 64-byte
        // page with NO report-id prefix (raw[0]=debounce, raw[2]=animation,
        // raw[3]=brightness); Windows HID prepends the report id (+1 to each).
        var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
        var brightIndex = animIndex + 1;
        if (raw.Length <= brightIndex) return false;

        var effect = KeebSettingsCodec.AnimationModeName(raw[animIndex]);
        var brightness = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
        var changed = false;
        _store.Update(s =>
        {
            if (effect is not null
                && !string.Equals(s.Keeb.FirmwareLighting.AnimationMode, effect, StringComparison.OrdinalIgnoreCase))
            {
                s.Keeb.FirmwareLighting.AnimationMode = effect;
                changed = true;
            }
            if (s.Keeb.FirmwareLighting.Brightness != brightness)
            {
                ApplyBrightnessToSettings(s, _hub.DeviceId, brightness);
                changed = true;
            }
        });
        if (changed)
        {
            ServiceLog.Info($"[keeb] device state synced -> effect={effect} brightness={brightness}");
            PanelTopics.BroadcastLighting(_panel);
        }
        return changed;
    }
}
