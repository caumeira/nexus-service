using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pushes the persisted keeb desired-state (game mode + firmware animation +
/// rotary) to the device as a single 0x06 settings write. Shared by:
///   - the REST provider, after any settings change,
///   - the connection worker, when the keeb first appears (so saved settings
///     take effect on plug-in),
///   - the lighting frame writer, when software streaming stops (re-asserts the
///     firmware animation, which streaming had suppressed).
/// Desired-state model: we always write the COMPLETE state from settings.json,
/// so a partial change can't clobber unrelated fields and we never need an
/// uncertain device read-back.
/// </summary>
public sealed class KeebSettingsApplier
{
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;

    public KeebSettingsApplier(KeebHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
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
            Console.Error.WriteLine($"[keeb] apply settings failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apply settings AND force the running firmware animation to re-initialise,
    /// so a brightness/palette change actually takes effect. The firmware only
    /// re-reads brightness/colour when the animation MODE changes (verified on
    /// hardware — a same-mode write is stored but ignored live), so we briefly
    /// write a different mode and then the real one. Used for the firmware-lighting
    /// path; game-mode / rotary use <see cref="Apply"/> (no re-init needed).
    /// </summary>
    public bool ApplyAndReinit()
    {
        if (!_hub.IsConnected) return false;
        try
        {
            var realPage = KeebSettingsCodec.BuildSettingsPage(_store.Load().Keeb);
            // page[3] is the animation-mode byte (page[0]=report id, [1]=debounce,
            // [2]=game mode, [3]=anim). Toggle Static<->Breathe for the transient.
            var transientPage = (byte[])realPage.Clone();
            transientPage[3] = realPage[3] == 0x01 ? (byte)0x02 : (byte)0x01;
            _hub.WriteSettings(transientPage);
            return _hub.WriteSettings(realPage);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[keeb] apply+reinit failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Read the device's current firmware effect and mirror a device-initiated
    /// change (e.g. the rotary middle button cycling the effect) into persisted
    /// state, so the panel reflects it. Our own writes keep device + persisted in
    /// sync, so only a hardware-side change produces a mismatch. Returns true if
    /// it updated the stored effect.
    /// </summary>
    public bool SyncEffectFromDevice()
    {
        if (!_hub.IsConnected) return false;
        var raw = _hub.ReadSettings();
        if (raw is null || raw.Length < 3) return false;
        // Linux hidraw read has no report-id prefix: [0]=debounce [1]=gameMode [2]=anim.
        var animByte = raw.Length >= 9 && raw[0] == 0x00 ? raw[3] : raw[2];
        var effect = KeebSettingsCodec.AnimationModeName(animByte);
        if (effect is null) return false;
        if (string.Equals(_store.Load().Keeb.FirmwareLighting.AnimationMode, effect, StringComparison.OrdinalIgnoreCase))
            return false;
        _store.Update(s => s.Keeb.FirmwareLighting.AnimationMode = effect);
        Console.Error.WriteLine($"[keeb] device effect -> {effect} (synced to UI)");
        return true;
    }
}
