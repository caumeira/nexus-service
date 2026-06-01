using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Keycode tables for the HYTE Keeb TKL, transcribed verbatim from
/// hyte-refs/hyte-documents/firmware-protocol/Keeb/KeyCodeDoc/keyassignment.md.
/// MatrixCode() returns the 4-byte key-matrix code for a (mode, func, input);
/// MacroHid() returns the single HID usage byte for a JS KeyboardEvent.code.
/// </summary>
public static class KeebKeyCodes
{
    // ── Category bytes (Byte 3 of every key-matrix code) ──
    private const byte CatStandard = 0x01; // standard / HID
    private const byte CatMouse = 0x02;
    private const byte CatMedia = 0x03; // media + system-media + web-media all live here
    private const byte CatSystem = 0x04;
    private const byte CatMacro = 0x05;
    private const byte CatLayer = 0xF0;

    // ── Macro Type bytes (Byte 2 of a macro code) ──
    private const byte MacroTypeNormal = 0x01;
    private const byte MacroTypeRepeat = 0x02;
    private const byte MacroTypeHoldAndPlay = 0x03;

    private static readonly byte[] None = { 0x00, 0x00, 0x00, 0x00 };

    /// <summary>
    /// 4-byte key-matrix code for a panel (mode, func, input). Byte 3 is the
    /// category (01 standard/HID, 02 mouse, 03 media, 04 system, 05 macro,
    /// F0 layer). Returns {0,0,0,0} (None / unassigned) for anything unknown.
    /// </summary>
    public static byte[] MatrixCode(string mode, string func, int? input)
    {
        switch (mode)
        {
            case "StandardKey":
                // None / PassThrough mean "no remap" → all zeros.
                if (func == "None" || func == "PassThrough")
                    return (byte[])None.Clone();
                if (StandardHid.TryGetValue(func, out var usage))
                    return new byte[] { usage, 0x00, 0x00, CatStandard };
                return (byte[])None.Clone();

            case "MouseKey":
                if (MouseCode.TryGetValue(func, out var mouse))
                {
                    // Wheel/pan ones carry the value in Byte 1; plain buttons keep 0.
                    var b1 = mouse.UsesValue ? (byte)(input ?? 0) : (byte)0x00;
                    return new byte[] { mouse.Code, b1, 0x00, CatMouse };
                }
                return (byte[])None.Clone();

            case "MediaKey":
                if (MediaCode.TryGetValue(func, out var media))
                    return new byte[] { media.B0, media.B1, 0x00, CatMedia };
                return (byte[])None.Clone();

            case "SystemMediaKey":
                if (SystemMediaCode.TryGetValue(func, out var sysMedia))
                    return new byte[] { sysMedia.B0, sysMedia.B1, 0x00, CatMedia };
                return (byte[])None.Clone();

            case "WebMediaKey":
                if (WebMediaCode.TryGetValue(func, out var webB0))
                    return new byte[] { webB0, 0x02, 0x00, CatMedia };
                return (byte[])None.Clone();

            case "SystemKey":
                if (SystemCode.TryGetValue(func, out var sys))
                    return new byte[] { sys, 0x00, 0x00, CatSystem };
                return (byte[])None.Clone();

            case "MacroKey":
                {
                    // func "Macro1".."Macro16" → 0-based index 0..15.
                    if (!TryParseMacroIndex(func, out var index))
                        return (byte[])None.Clone();
                    var type = input switch
                    {
                        2 => MacroTypeRepeat,
                        3 => MacroTypeHoldAndPlay,
                        _ => MacroTypeNormal, // null or 1 (or anything else) → Normal
                    };
                    return new byte[] { index, 0x00, type, CatMacro };
                }

            case "LayerKey":
                // Doc only spells out MO (15 <0-3> 00 F0); MO/TG/TO/DF all use
                // Byte 0 = 0x15 with the target layer in Byte 1 per the spec.
                switch (func)
                {
                    case "MOSwitch":
                    case "TGSwitch":
                    case "TOSwitch":
                    case "DFSwitch":
                        return new byte[] { 0x15, (byte)(input ?? 0), 0x00, CatLayer };
                    default:
                        return (byte[])None.Clone();
                }

            // TODO: RGB keys (mode "RGBKey") and Software keys (mode "SoftwareKey")
            // are not in the firmware keycode doc — they are handled software-side
            // and have no firmware remap, so we return {0,0,0,0} (the caller still
            // gets a non-null array to persist/handle).
            case "RGBKey":
            case "SoftwareKey":
                return (byte[])None.Clone();

            default:
                return (byte[])None.Clone();
        }
    }

    /// <summary>HID usage byte for a JS KeyboardEvent.code (e.g. "KeyA"=>0x04). 0 if unknown.</summary>
    public static byte MacroHid(string eventCode)
        => MacroHidUsage.TryGetValue(eventCode, out var usage) ? usage : (byte)0x00;

    private static bool TryParseMacroIndex(string func, out byte index)
    {
        index = 0;
        if (func.Length < 6 || !func.StartsWith("Macro", StringComparison.Ordinal))
            return false;
        if (!int.TryParse(func.AsSpan(5), out var n) || n < 1 || n > 16)
            return false;
        index = (byte)(n - 1);
        return true;
    }

    private readonly struct MouseEntry
    {
        public readonly byte Code;
        public readonly bool UsesValue;
        public MouseEntry(byte code, bool usesValue)
        {
            Code = code;
            UsesValue = usesValue;
        }
    }

    private readonly struct MediaEntry
    {
        public readonly byte B0;
        public readonly byte B1;
        public MediaEntry(byte b0, byte b1)
        {
            B0 = b0;
            B1 = b1;
        }
    }

    // ── StandardKey: web func name → HID usage (Byte 0) ──
    private static readonly Dictionary<string, byte> StandardHid = new(System.StringComparer.Ordinal)
    {
        // Letters A..Z → 0x04..0x1D
        ["A"] = 0x04, ["B"] = 0x05, ["C"] = 0x06, ["D"] = 0x07, ["E"] = 0x08,
        ["F"] = 0x09, ["G"] = 0x0A, ["H"] = 0x0B, ["I"] = 0x0C, ["J"] = 0x0D,
        ["K"] = 0x0E, ["L"] = 0x0F, ["M"] = 0x10, ["N"] = 0x11, ["O"] = 0x12,
        ["P"] = 0x13, ["Q"] = 0x14, ["R"] = 0x15, ["S"] = 0x16, ["T"] = 0x17,
        ["U"] = 0x18, ["V"] = 0x19, ["W"] = 0x1A, ["X"] = 0x1B, ["Y"] = 0x1C,
        ["Z"] = 0x1D,
        // Numbers 1..9 → 0x1E..0x26, 0 → 0x27
        ["Number1"] = 0x1E, ["Number2"] = 0x1F, ["Number3"] = 0x20, ["Number4"] = 0x21,
        ["Number5"] = 0x22, ["Number6"] = 0x23, ["Number7"] = 0x24, ["Number8"] = 0x25,
        ["Number9"] = 0x26, ["Number0"] = 0x27,
        ["Return"] = 0x28, ["Escape"] = 0x29, ["Backspace"] = 0x2A, ["Tab"] = 0x2B,
        ["Space"] = 0x2C, ["Minus"] = 0x2D, ["Equals"] = 0x2E, ["LeftBracket"] = 0x2F,
        ["RightBracket"] = 0x30, ["Backslash"] = 0x31, ["NonUsPound"] = 0x32,
        ["Europe1"] = 0x32, // Europe 1 region, same usage as NonUsPound
        ["Semicolon"] = 0x33, ["Quote"] = 0x34, ["Backtick"] = 0x35, ["Comma"] = 0x36,
        ["Period"] = 0x37, ["Slash"] = 0x38, ["CapsLock"] = 0x39,
        // F1..F12 → 0x3A..0x45
        ["F1"] = 0x3A, ["F2"] = 0x3B, ["F3"] = 0x3C, ["F4"] = 0x3D, ["F5"] = 0x3E,
        ["F6"] = 0x3F, ["F7"] = 0x40, ["F8"] = 0x41, ["F9"] = 0x42, ["F10"] = 0x43,
        ["F11"] = 0x44, ["F12"] = 0x45,
        ["PrintScreen"] = 0x46, ["ScrollLock"] = 0x47, ["Pause"] = 0x48, ["Insert"] = 0x49,
        ["Home"] = 0x4A, ["PageUp"] = 0x4B, ["Delete"] = 0x4C, ["End"] = 0x4D,
        ["PageDown"] = 0x4E, ["RightArrow"] = 0x4F, ["LeftArrow"] = 0x50,
        ["DownArrow"] = 0x51, ["UpArrow"] = 0x52, ["NumLock"] = 0x53,
        ["KeypadSlash"] = 0x54, ["KeypadAsterisk"] = 0x55, ["KeypadMinus"] = 0x56,
        ["KeypadPlus"] = 0x57, ["KeypadEnter"] = 0x58, ["Keypad1End"] = 0x59,
        ["Keypad2DownArrow"] = 0x5A, ["Keypad3PageDown"] = 0x5B, ["Keypad4LeftArrow"] = 0x5C,
        ["Keypad5"] = 0x5D, ["Keypad6RightArrow"] = 0x5E, ["Keypad7Home"] = 0x5F,
        ["Keypad8UpArrow"] = 0x60, ["Keypad9PageUp"] = 0x61, ["Keypad0Insert"] = 0x62,
        ["KeypadPeriodDelete"] = 0x63,
        ["NonUsBackslash"] = 0x64, // Europe 2
        ["Application"] = 0x65, ["KeypadEqual"] = 0x67,
        // F13..F24 → 0x68..0x73
        ["F13"] = 0x68, ["F14"] = 0x69, ["F15"] = 0x6A, ["F16"] = 0x6B, ["F17"] = 0x6C,
        ["F18"] = 0x6D, ["F19"] = 0x6E, ["F20"] = 0x6F, ["F21"] = 0x70, ["F22"] = 0x71,
        ["F23"] = 0x72, ["F24"] = 0x73,
        ["LeftControl"] = 0xE0, ["LeftShift"] = 0xE1, ["LeftAlt"] = 0xE2, ["LeftGUI"] = 0xE3,
        ["RightControl"] = 0xE4, ["RightShift"] = 0xE5, ["RightAlt"] = 0xE6, ["RightGUI"] = 0xE7,
    };

    // ── MouseKey: web func → (Byte 0 code, carries the input Value in Byte 1?) ──
    private static readonly Dictionary<string, MouseEntry> MouseCode = new(System.StringComparer.Ordinal)
    {
        ["MouseLButton"] = new MouseEntry(0xF0, false),
        ["MouseRButton"] = new MouseEntry(0xF1, false),
        ["MouseMButton"] = new MouseEntry(0xF2, false),
        ["MouseB4Button"] = new MouseEntry(0xF3, false),
        ["MouseB5Button"] = new MouseEntry(0xF4, false),
        ["MouseWheelUp"] = new MouseEntry(0xF5, true),
        ["MouseWheelDown"] = new MouseEntry(0xF6, true),
        ["MouseACPanLeft"] = new MouseEntry(0xF7, true),
        ["MouseACPanRight"] = new MouseEntry(0xF8, true),
        ["MouseXPanLeft"] = new MouseEntry(0xF9, true),
        ["MouseXPanRight"] = new MouseEntry(0xFA, true),
        ["MouseXPanUp"] = new MouseEntry(0xFB, true),   // doc: "Mouse Y Pan up"
        ["MouseXPanDown"] = new MouseEntry(0xFC, true), // doc: "Mouse Y Pan down"
    };

    // ── MediaKey: web func → (Byte 0, Byte 1) ──
    private static readonly Dictionary<string, MediaEntry> MediaCode = new(System.StringComparer.Ordinal)
    {
        ["FastForward"] = new MediaEntry(0xB3, 0x00),
        ["Rewind"] = new MediaEntry(0xB4, 0x00),
        ["ScanNextTrack"] = new MediaEntry(0xB5, 0x00),
        ["ScanPreviousTrack"] = new MediaEntry(0xB6, 0x00),
        ["Stop"] = new MediaEntry(0xB7, 0x00),
        ["PlayAndPause"] = new MediaEntry(0xCD, 0x00),
        ["Mute"] = new MediaEntry(0xE2, 0x00),
        ["VolumeUp"] = new MediaEntry(0xE9, 0x00),
        ["VolumeDown"] = new MediaEntry(0xEA, 0x00),
    };

    // ── SystemMediaKey: web func → (Byte 0, Byte 1=0x01) ──
    private static readonly Dictionary<string, MediaEntry> SystemMediaCode = new(System.StringComparer.Ordinal)
    {
        ["MediaSelect"] = new MediaEntry(0x83, 0x01),
        ["Mail"] = new MediaEntry(0x8A, 0x01),
        ["Calculator"] = new MediaEntry(0x92, 0x01),
        ["MyComputer"] = new MediaEntry(0x94, 0x01),
    };

    // ── WebMediaKey: web func → Byte 0 (Byte 1 fixed at 0x02) ──
    private static readonly Dictionary<string, byte> WebMediaCode = new(System.StringComparer.Ordinal)
    {
        ["WebSearch"] = 0x21,
        ["WebHome"] = 0x23,
        ["WebBack"] = 0x24,
        ["WebForward"] = 0x25,
        ["WebStop"] = 0x26,
        ["WebRefresh"] = 0x27,
        ["WebFavorite"] = 0x2A,
    };

    // ── SystemKey: web func → Byte 0 ──
    private static readonly Dictionary<string, byte> SystemCode = new(System.StringComparer.Ordinal)
    {
        ["Power"] = 0x01,
        ["Sleep"] = 0x02,
        ["Wake"] = 0x04,
    };

    // ── MacroHid: JS KeyboardEvent.code → HID usage byte ──
    private static readonly Dictionary<string, byte> MacroHidUsage = new(System.StringComparer.Ordinal)
    {
        ["KeyA"] = 0x04, ["KeyB"] = 0x05, ["KeyC"] = 0x06, ["KeyD"] = 0x07, ["KeyE"] = 0x08,
        ["KeyF"] = 0x09, ["KeyG"] = 0x0A, ["KeyH"] = 0x0B, ["KeyI"] = 0x0C, ["KeyJ"] = 0x0D,
        ["KeyK"] = 0x0E, ["KeyL"] = 0x0F, ["KeyM"] = 0x10, ["KeyN"] = 0x11, ["KeyO"] = 0x12,
        ["KeyP"] = 0x13, ["KeyQ"] = 0x14, ["KeyR"] = 0x15, ["KeyS"] = 0x16, ["KeyT"] = 0x17,
        ["KeyU"] = 0x18, ["KeyV"] = 0x19, ["KeyW"] = 0x1A, ["KeyX"] = 0x1B, ["KeyY"] = 0x1C,
        ["KeyZ"] = 0x1D,
        ["Digit1"] = 0x1E, ["Digit2"] = 0x1F, ["Digit3"] = 0x20, ["Digit4"] = 0x21,
        ["Digit5"] = 0x22, ["Digit6"] = 0x23, ["Digit7"] = 0x24, ["Digit8"] = 0x25,
        ["Digit9"] = 0x26, ["Digit0"] = 0x27,
        ["Enter"] = 0x28, ["Escape"] = 0x29, ["Backspace"] = 0x2A, ["Tab"] = 0x2B,
        ["Space"] = 0x2C, ["Minus"] = 0x2D, ["Equal"] = 0x2E, ["BracketLeft"] = 0x2F,
        ["BracketRight"] = 0x30, ["Backslash"] = 0x31, ["Semicolon"] = 0x33, ["Quote"] = 0x34,
        ["Backquote"] = 0x35, ["Comma"] = 0x36, ["Period"] = 0x37, ["Slash"] = 0x38,
        ["CapsLock"] = 0x39,
        ["F1"] = 0x3A, ["F2"] = 0x3B, ["F3"] = 0x3C, ["F4"] = 0x3D, ["F5"] = 0x3E,
        ["F6"] = 0x3F, ["F7"] = 0x40, ["F8"] = 0x41, ["F9"] = 0x42, ["F10"] = 0x43,
        ["F11"] = 0x44, ["F12"] = 0x45,
        ["PrintScreen"] = 0x46, ["ScrollLock"] = 0x47, ["Pause"] = 0x48, ["Insert"] = 0x49,
        ["Home"] = 0x4A, ["PageUp"] = 0x4B, ["Delete"] = 0x4C, ["End"] = 0x4D,
        ["PageDown"] = 0x4E, ["ArrowRight"] = 0x4F, ["ArrowLeft"] = 0x50,
        ["ArrowDown"] = 0x51, ["ArrowUp"] = 0x52, ["NumLock"] = 0x53,
        ["NumpadDivide"] = 0x54, ["NumpadMultiply"] = 0x55, ["NumpadSubtract"] = 0x56,
        ["NumpadAdd"] = 0x57, ["NumpadEnter"] = 0x58,
        ["Numpad1"] = 0x59, ["Numpad2"] = 0x5A, ["Numpad3"] = 0x5B, ["Numpad4"] = 0x5C,
        ["Numpad5"] = 0x5D, ["Numpad6"] = 0x5E, ["Numpad7"] = 0x5F, ["Numpad8"] = 0x60,
        ["Numpad9"] = 0x61, ["Numpad0"] = 0x62, ["NumpadDecimal"] = 0x63,
        ["ControlLeft"] = 0xE0, ["ShiftLeft"] = 0xE1, ["AltLeft"] = 0xE2, ["MetaLeft"] = 0xE3,
        ["ControlRight"] = 0xE4, ["ShiftRight"] = 0xE5, ["AltRight"] = 0xE6, ["MetaRight"] = 0xE7,
        ["ContextMenu"] = 0x65,
        // F13..F24 → 0x68..0x73
        ["F13"] = 0x68, ["F14"] = 0x69, ["F15"] = 0x6A, ["F16"] = 0x6B, ["F17"] = 0x6C,
        ["F18"] = 0x6D, ["F19"] = 0x6E, ["F20"] = 0x6F, ["F21"] = 0x70, ["F22"] = 0x71,
        ["F23"] = 0x72, ["F24"] = 0x73,
    };
}
