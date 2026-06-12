using System.Collections.Generic;
using Nexus.Service.Models.Common;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Keeb;

public sealed class StubKeebProvider : IKeebProvider, IInputterProvider
{
    private readonly IConfigStore _store;

    public StubKeebProvider(IConfigStore store) { _store = store; }

    public KeyboardState GetState(int layer) => new()
    {
        IsConnected = false,
        Profile = 0,
        Layer = layer,
        Layout = "ANSI",
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
            RotaryLeft = k.RotaryLeft,
            RotaryRight = k.RotaryRight,
            RotarySensitivity = k.RotarySensitivity,
            AnimationMode = k.FirmwareLighting.AnimationMode,
            Speed = k.FirmwareLighting.Speed,
            Direction = k.FirmwareLighting.Direction,
            Brightness = k.FirmwareLighting.Brightness,
            KeyIndicator = k.FirmwareLighting.KeyIndicator,
            KeyReactive = k.FirmwareLighting.KeyReactive,
            KeyReactiveMask = k.FirmwareLighting.KeyReactiveMask,
            KeyReactiveMode = k.FirmwareLighting.KeyReactiveMode,
            KeyReactiveColor = new RGBA
            {
                R = k.FirmwareLighting.KeyReactiveColor.R,
                G = k.FirmwareLighting.KeyReactiveColor.G,
                B = k.FirmwareLighting.KeyReactiveColor.B,
                A = k.FirmwareLighting.KeyReactiveColor.A,
            },
        };
    }

    public string[] GetRotaryFunctions() => new[]
    {
        "VolumeAdjustment", "BrightnessAdjustment", "Scale", "AltTab", "ScrollX", "ScrollY",
    };

    public void SetRotary(SetRotaryWheelsBody body) => _store.Update(s =>
    {
        s.Keeb.RotaryLeft = body.Left;
        s.Keeb.RotaryRight = body.Right;
        s.Keeb.RotaryApps.Clear();
        foreach (var a in body.Apps)
        {
            s.Keeb.RotaryApps.Add(new KeebRotaryAppOverride { TargetId = a.TargetId, Left = a.Left, Right = a.Right });
        }
    });

    public void SetRotarySensitivity(string sensitivity) =>
        _store.Update(s => s.Keeb.RotarySensitivity = sensitivity);

    public void SetKeyReactive(SetFirmwareLightingBody body) => SetFirmwareLighting(body);

    public void SetFirmwareLighting(SetFirmwareLightingBody body) => _store.Update(s =>
    {
        s.Keeb.FirmwareLighting.AnimationMode = body.AnimationMode;
        s.Keeb.FirmwareLighting.Speed = body.Speed;
        s.Keeb.FirmwareLighting.Direction = body.Direction;
        s.Keeb.FirmwareLighting.Brightness = body.Brightness;
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
        s.Keeb.FirmwareLighting.KeyIndicator = body.KeyIndicator;
    });

    public void SetPassiveLighting(SetPassiveLightingBody body) => _store.Update(s =>
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

    public void SetGameMode(SetGameModeBody body) => _store.Update(s =>
    {
        s.Keeb.GameMode.AltF4 = body.AltF4;
        s.Keeb.GameMode.AltTab = body.AltTab;
        s.Keeb.GameMode.ShiftTab = body.ShiftTab;
        s.Keeb.GameMode.WindowsKey = body.WindowsKey;
    });

    public KeebMacro GetMacro(int index)
    {
        var s = _store.Load();
        if (s.Keeb.Macros.TryGetValue(index, out var doc))
        {
            return Convert(doc);
        }
        return new() { Index = index };
    }

    public KeebMacro SetMacro(int index, SetMacroBody body)
    {
        _store.Update(s =>
        {
            var doc = new KeebMacroDocument { Index = index };
            foreach (var k in body.Keys)
            {
                doc.Keys.Add(new Persistence.KeebMacroKey
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
        });
        return GetMacro(index);
    }

    public void Send(InputterBody body)
    {
    }

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
