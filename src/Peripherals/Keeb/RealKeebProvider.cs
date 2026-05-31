using System;
using System.Collections.Generic;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// HID-backed <see cref="IKeebProvider"/>. Every setter persists the desired
/// state to settings.json (the single source of truth) and then pushes the
/// COMPLETE state to the keyboard via <see cref="KeebSettingsApplier"/> — one
/// 0x06 settings write covering game mode + firmware animation + rotary — so a
/// partial change never clobbers another field. Replaces
/// <see cref="StubKeebProvider"/>'s persist-only behaviour.
///
/// Reads return the persisted desired-state (which equals what we last pushed),
/// plus live connection status from <see cref="KeebHub"/>. The firmware
/// animation only shows when no software effect is streaming — the lighting
/// frame writer arbitrates that (re-asserting these settings when streaming
/// stops), matching the panel's "applies when nexus isn't driving the LEDs".
///
/// Macros / per-key layer assignment persist here today; their device writes
/// (0xF3 / 0xF2) land in the macro + layer phases on this branch.
/// </summary>
public sealed class RealKeebProvider : IKeebProvider
{
    private readonly IConfigStore _store;
    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;

    public RealKeebProvider(IConfigStore store, KeebHub hub, KeebSettingsApplier applier)
    {
        _store = store;
        _hub = hub;
        _applier = applier;
    }

    public KeyboardState GetState(int layer) => new()
    {
        IsConnected = _hub.IsConnected,
        Profile = _hub.State.Profile,
        Layer = layer,
        Layout = string.IsNullOrEmpty(_hub.State.Layout) ? "ANSI" : _hub.State.Layout,
        // Per-key assignment overlay is empty until layer remapping lands (the
        // web renders default legends from its own layout); connection + layout
        // are live so the modal + the other tabs reflect real device state.
        Keys = new List<List<KeebKey>>(),
    };

    public GetKeebSettingsResponse GetSettings()
    {
        var k = _store.Load().Keeb;
        return new()
        {
            AltF4Disabled = k.GameMode.AltF4,
            AltTabDisabled = k.GameMode.AltTab,
            ShiftKeyDisabled = k.GameMode.ShiftTab,
            WindowsKeyDisabled = k.GameMode.WindowsKey,
            AnimationMode = k.FirmwareLighting.AnimationMode,
            Speed = k.FirmwareLighting.Speed,
            Direction = k.FirmwareLighting.Direction,
            Brightness = k.FirmwareLighting.Brightness,
            KeyIndicator = k.FirmwareLighting.KeyIndicator,
            KeyReactive = k.FirmwareLighting.KeyReactive,
            KeyReactiveMask = k.FirmwareLighting.KeyReactiveMask,
            KeyReactiveMode = k.FirmwareLighting.KeyReactiveMode,
            KeyReactiveColor = ToRgba(k.FirmwareLighting.KeyReactiveColor),
        };
    }

    public string[] GetRotaryFunctions() => KeebSettingsCodec.RotaryFunctions;

    public void SetRotary(SetRotaryWheelsBody body)
    {
        _store.Update(s =>
        {
            s.Keeb.RotaryLeft = body.Left;
            s.Keeb.RotaryRight = body.Right;
            s.Keeb.RotaryApps.Clear();
            foreach (var a in body.Apps)
                s.Keeb.RotaryApps.Add(new KeebRotaryAppOverride { TargetId = a.TargetId, Left = a.Left, Right = a.Right });
        });
        _applier.Apply();
    }

    public void SetRotarySensitivity(string sensitivity)
    {
        // Sensitivity has no slot in the 0x06 settings payload (reserved bytes);
        // persist it so the panel round-trips. Encoder feel is firmware-default.
        _store.Update(s => s.Keeb.RotarySensitivity = sensitivity);
    }

    public void SetKeyReactive(SetFirmwareLightingBody body) => SetFirmwareLighting(body);

    public void SetFirmwareLighting(SetFirmwareLightingBody body)
    {
        var brightness = Math.Clamp(body.Brightness, 0, 100);
        _store.Update(s =>
        {
            s.Keeb.FirmwareLighting.AnimationMode = body.AnimationMode;
            s.Keeb.FirmwareLighting.Speed = body.Speed;
            s.Keeb.FirmwareLighting.Direction = body.Direction;
            s.Keeb.FirmwareLighting.Brightness = brightness;
            s.Keeb.FirmwareLighting.KeyIndicator = body.KeyIndicator;

            // The Settings "Brightness" must dim the keyboard whether the
            // firmware animation OR a software effect is showing. The firmware
            // byte (written by the applier below) only scales the firmware
            // animation — so a software effect would ignore it. Mirror the
            // value onto the keeb zones' software-stream brightness too, so the
            // frame writer dims the streamed output to match.
            var hubId = _hub.DeviceId;
            if (!string.IsNullOrEmpty(hubId))
            {
                foreach (var id in new[]
                {
                    hubId + Nexus.Service.Lighting.KeebLightingDeviceProvider.KeysSuffix,
                    hubId + Nexus.Service.Lighting.KeebLightingDeviceProvider.UnderglowSuffix,
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
        });
        _applier.Apply();
    }

    public void SetPassiveLighting(SetPassiveLightingBody body)
    {
        // Key-reactive is a software overlay driven by input callbacks; persist
        // it here and render it in the input-callback phase. No firmware bytes.
        _store.Update(s =>
        {
            s.Keeb.FirmwareLighting.KeyReactive = body.KeyReactive;
            s.Keeb.FirmwareLighting.KeyReactiveMask = body.KeyReactiveMask;
            s.Keeb.FirmwareLighting.KeyReactiveMode = body.KeyReactiveMode;
            s.Keeb.FirmwareLighting.KeyReactiveColor = new RgbaColor
            {
                R = body.KeyReactiveColor.R,
                G = body.KeyReactiveColor.G,
                B = body.KeyReactiveColor.B,
                A = body.KeyReactiveColor.A,
            };
        });
    }

    public void SetGameMode(SetGameModeBody body)
    {
        _store.Update(s =>
        {
            s.Keeb.GameMode.AltF4 = body.AltF4;
            s.Keeb.GameMode.AltTab = body.AltTab;
            s.Keeb.GameMode.ShiftTab = body.ShiftTab;
            s.Keeb.GameMode.WindowsKey = body.WindowsKey;
        });
        _applier.Apply();
    }

    public KeebMacro GetMacro(int index)
    {
        var s = _store.Load();
        return s.Keeb.Macros.TryGetValue(index, out var doc) ? Convert(doc) : new KeebMacro { Index = index };
    }

    public KeebMacro SetMacro(int index, SetMacroBody body)
    {
        KeebMacroDocument? saved = null;
        _store.Update(s =>
        {
            var doc = new KeebMacroDocument { Index = index };
            foreach (var k in body.Keys)
            {
                doc.Keys.Add(new KeebMacroKey
                {
                    Key = k.Key,
                    Duration = k.Duration,
                    Type = k.Type,
                    Category = k.Category,
                    Meta = k.Meta,
                    Ctrl = k.Ctrl,
                    Alt = k.Alt,
                    Shift = k.Shift,
                });
            }
            s.Keeb.Macros[index] = doc;
            saved = doc;
        });
        // Push the macro to the keyboard's onboard storage (0xF3). Best-effort:
        // returns false when disconnected, leaving the persisted copy to be
        // applied later — but when connected this is a real device write.
        if (saved is not null) _hub.WriteMacro(index, KeebMacroCodec.BuildMacroPages(saved));
        return GetMacro(index);
    }

    private static RGBA ToRgba(RgbaColor c) => new() { R = c.R, G = c.G, B = c.B, A = c.A };

    private static KeebMacro Convert(KeebMacroDocument doc)
    {
        var m = new KeebMacro { Index = doc.Index };
        foreach (var k in doc.Keys)
        {
            m.Keys.Add(new MacroKey
            {
                Key = k.Key,
                Duration = k.Duration,
                Type = k.Type,
                Category = k.Category,
                Meta = k.Meta,
                Ctrl = k.Ctrl,
                Alt = k.Alt,
                Shift = k.Shift,
            });
        }
        return m;
    }
}
