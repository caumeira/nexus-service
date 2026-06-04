using System;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

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
            ServiceLog.Error($"[keeb] apply settings failed: {ex.GetType().Name}: {ex.Message}");
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
        if (raw is null) return false;
        // Read layout (verified on the bench): Linux hidraw returns the 64-byte
        // page with NO report-id prefix (raw[0]=debounce, raw[2]=animation);
        // Windows HID prepends the report id (raw[0]=0x00, raw[3]=animation).
        var animIndex = OperatingSystem.IsWindows() ? 3 : 2;
        if (raw.Length <= animIndex) return false;
        var effect = KeebSettingsCodec.AnimationModeName(raw[animIndex]);
        if (effect is null) return false;
        if (string.Equals(_store.Load().Keeb.FirmwareLighting.AnimationMode, effect, StringComparison.OrdinalIgnoreCase))
            return false;
        _store.Update(s => s.Keeb.FirmwareLighting.AnimationMode = effect);
        ServiceLog.Info($"[keeb] device effect -> {effect} (synced to UI)");
        return true;
    }
}
