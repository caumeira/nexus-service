using System;
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
/// without a host callback - which <see cref="SyncFromDevice"/> reads back into
/// the keeb master brightness so the panel and software stream follow the knob.
/// FirmwareLighting.Brightness is that master: it both dims the firmware
/// animation (the byte) and multiplies the software stream (the frame writer).
/// </summary>
public sealed class KeebSettingsApplier
{
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _panel;
    // Serializes a host settings write (store mutate + byte write) against the
    // device read-back, so the poll can't read a pre-write byte and clobber the
    // value a Settings-slider write just stored before its byte reaches the device.
    private readonly object _gate = new();

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
    /// Mutate the persisted keeb state and write the full page to the device as one
    /// gated step, so the read-back poll can't interleave between the store mutate
    /// and the byte write. Used by every host-initiated settings change.
    /// </summary>
    public bool ApplyGated(Action<NexusSettings> mutate)
    {
        lock (_gate)
        {
            _store.Update(mutate);
            return Apply();
        }
    }

    /// <summary>
    /// Read the device's current firmware effect and brightness and mirror a
    /// device-initiated change into persisted state, so the panel and the software
    /// stream follow the hardware. In firmware rotary mode the middle button cycles
    /// the effect and the knob moves the brightness byte with NO host callback, so
    /// the connection worker calls this each poll tick. Adopts the byte into the
    /// keeb master (FirmwareLighting.Brightness) only - never the per-zone software
    /// prefs, which stay the user's LED-map values. The equality check makes our own
    /// writes a no-op, so only a hardware-side change broadcasts. Gated against
    /// <see cref="ApplyGated"/> so it can't clobber an in-flight host write.
    /// </summary>
    public bool SyncFromDevice()
    {
        if (!_hub.IsConnected) return false;
        string? effect = null;
        var brightness = 0;
        var changed = false;
        lock (_gate)
        {
            var raw = _hub.ReadSettings();
            if (raw is null) return false;
            // Read layout (verified on the bench): Linux hidraw returns the 64-byte
            // page with NO report-id prefix (raw[0]=debounce, raw[2]=animation,
            // raw[3]=brightness); Windows HID prepends the report id (+1 to each).
            var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
            var brightIndex = animIndex + 1;
            if (raw.Length <= brightIndex) return false;

            effect = KeebSettingsCodec.AnimationModeName(raw[animIndex]);
            brightness = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
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
                    s.Keeb.FirmwareLighting.Brightness = brightness;
                    changed = true;
                }
            });
        }
        if (changed)
        {
            ServiceLog.Info($"[keeb] device state synced -> effect={effect} brightness={brightness}");
            PanelTopics.BroadcastLighting(_panel);
        }
        return changed;
    }
}
