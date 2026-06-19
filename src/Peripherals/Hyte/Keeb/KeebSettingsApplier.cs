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
/// the firmware rotary cycles the effect and moves the brightness byte without a
/// host callback - which <see cref="SyncFromDevice"/> reads back.
/// FirmwareLighting.Brightness dims only the firmware animation (the byte), so
/// when the firmware animation is showing the knob drives it directly. While a
/// software effect streams the firmware animation is suppressed; the knob then steps
/// system-wide GlobalBrightness up/down via <see cref="TrackKnobAndEaseGlobal"/> - a
/// relative up/down control with its own level, eased, never mirroring the byte position.
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
    // Last device bytes SyncFromDevice saw. It adopts a byte only when it changes,
    // so a redundant read can't re-apply the same value and bounce the brightness.
    // Touched only under _gate.
    private int _lastBrightByte = -1;
    private int _lastAnimByte = -1;
    // Streaming knob -> global state, all under _gate. The firmware knob is a rotary
    // encoder; the host only sees its 0-100 brightness byte, which pins at the ends. So
    // _knobTargetGlobal is the software's OWN level - it does NOT mirror the byte. Each
    // poll the byte moved steps it up/down by KnobStep, like an up/down key, so the level's
    // range is independent of where the byte sits (no start-position cap). The stored global
    // eases toward it so the dimming glides. A knob spun past the firmware's extreme pins the
    // byte and stops registering until reversed - the direction we can't read in firmware mode.
    private const int KnobGlitchMaxPct = 35;     // a single-poll jump this big is a misread
    private const float KnobStep = 0.04f;        // level step per poll the byte moved
    private const float KnobEaseFactor = 0.3f;   // per-tick fraction of the gap to the target
    private const int KnobEaseActiveTicks = 20;  // keep easing this many ticks past the last move
    private const int KnobBroadcastEveryTicks = 3;
    private int _lastKnobPct = -1;
    private float _knobTargetGlobal;
    private int _knobActiveTicks;
    private int _knobBroadcastTicks;

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
    /// Seed the software level from the current global so the knob steps continue from
    /// wherever the brightness already is (no jump), and re-reference the byte. The frame
    /// writer calls this when a software effect starts streaming.
    /// </summary>
    public void ResetKnobBaseline()
    {
        lock (_gate)
        {
            _lastKnobPct = -1;
            _knobTargetGlobal = Math.Clamp(_store.Load().Lighting.GlobalBrightness, 0f, 1f);
            _knobActiveTicks = 0;
        }
    }

    /// <summary>
    /// Per-frame-tick knob -> global while a software effect streams. On <paramref
    /// name="readByte"/> ticks it reads the firmware brightness byte and, when it moved,
    /// steps the software's own level (_knobTargetGlobal) up or down by KnobStep - a
    /// relative up/down control, never mirrored onto the byte's position, so the byte's
    /// start point and end-stops don't cap the reachable range. Every tick the stored
    /// global eases toward that level, so the dimming glides instead of stepping. A lone
    /// implausible jump is skipped as a misread. The firmware byte pins at 0/100, so
    /// turning past the firmware's extreme stops registering until reversed.
    /// </summary>
    public void TrackKnobAndEaseGlobal(bool readByte)
    {
        if (!_hub.IsConnected) return;
        var broadcast = false;
        lock (_gate)
        {
            if (readByte)
            {
                var raw = _hub.ReadSettings();
                var brightIndex = (OperatingSystem.IsWindows() ? 3 : 2) + 1;
                if (raw is not null && raw.Length > brightIndex)
                {
                    var pct = KeebSettingsCodec.BrightnessPercentFromByte(raw[brightIndex]);
                    if (_lastKnobPct < 0)
                    {
                        _lastKnobPct = pct;
                    }
                    else if (pct != _lastKnobPct)
                    {
                        if (Math.Abs(pct - _lastKnobPct) <= KnobGlitchMaxPct)
                        {
                            var step = pct > _lastKnobPct ? KnobStep : -KnobStep;
                            _knobTargetGlobal = Math.Clamp(_knobTargetGlobal + step, 0f, 1f);
                            _knobActiveTicks = KnobEaseActiveTicks;
                        }
                        _lastKnobPct = pct;
                    }
                }
            }
            if (_knobActiveTicks <= 0) return;
            _knobActiveTicks--;
            var cur = _store.Load().Lighting.GlobalBrightness;
            var next = cur + (_knobTargetGlobal - cur) * KnobEaseFactor;
            if (Math.Abs(_knobTargetGlobal - next) < 0.004f) next = _knobTargetGlobal;
            if (Math.Abs(next - cur) <= 0.0008f) return;
            _store.Update(s => s.Lighting.GlobalBrightness = next);
            broadcast = ++_knobBroadcastTicks >= KnobBroadcastEveryTicks;
            if (broadcast) _knobBroadcastTicks = 0;
        }
        if (broadcast) PanelTopics.BroadcastLighting(_panel);
    }

    /// <summary>
    /// Mutate the persisted keeb state and (when <paramref name="writeDevice"/>)
    /// write the full page to the device, as one gated step so the read-back poll
    /// can't interleave between the store mutate and the byte write. Callers pass
    /// writeDevice=false while a software effect streams: the 0x06 page write would
    /// flash the firmware animation through the live stream, and the frame writer
    /// re-writes the page when streaming stops anyway.
    /// </summary>
    public bool ApplyGated(Action<NexusSettings> mutate, bool writeDevice = true)
    {
        lock (_gate)
        {
            _store.Update(mutate);
            return writeDevice && Apply();
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

            int animByte = raw[animIndex];
            int brightByte = raw[brightIndex];
            // Adopt only what the device actually changed since the last read. An
            // unchanged byte is skipped, so two pollers reading the same value never
            // fight, and a host-set master (written to the store but not the device
            // while streaming) survives reading back the unchanged knob byte.
            var animChanged = animByte != _lastAnimByte;
            var brightChanged = brightByte != _lastBrightByte;
            _lastAnimByte = animByte;
            _lastBrightByte = brightByte;
            if (!animChanged && !brightChanged) return false;

            effect = KeebSettingsCodec.AnimationModeName((byte)animByte);
            brightness = KeebSettingsCodec.BrightnessPercentFromByte((byte)brightByte);
            // The byte is the firmware animation brightness; adopt it as the keeb
            // master. The connection worker only calls this with no software effect
            // (the firmware animation showing); while streaming the frame writer reads
            // the byte instead, as a delta onto global - see NudgeGlobalFromKnob.
            _store.Update(s =>
            {
                if (animChanged && effect is not null
                    && !string.Equals(s.Keeb.FirmwareLighting.AnimationMode, effect, StringComparison.OrdinalIgnoreCase))
                {
                    s.Keeb.FirmwareLighting.AnimationMode = effect;
                    changed = true;
                }
                if (brightChanged && s.Keeb.FirmwareLighting.Brightness != brightness)
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
