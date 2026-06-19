using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Encodes the persisted keeb desired-state into the 0x06 settings page.
/// We always emit the COMPLETE state (game mode + firmware animation + rotary
/// in one page) so changing one field never clobbers another on the device.
/// Byte layout: hyte-refs firmware-protocol/Keeb/1-set-get-keyboard-setting.md,
/// cross-checked against the legacy KeebSettings.GetCommands() and the rotary
/// keycode table (scrollwheel-keycode.md).
/// </summary>
public static class KeebSettingsCodec
{
    // ── Rotary function ids (byte 0 of each 4-byte encoder code) ──
    public const byte RotaryVolume = 0x00;
    public const byte RotaryBrightness = 0x01;
    public const byte RotaryScale = 0x02;
    public const byte RotaryAltTab = 0x03;
    public const byte RotaryCtrlTab = 0x04;
    public const byte RotaryScrollY = 0x05; // vertical
    public const byte RotaryScrollX = 0x06; // horizontal
    public const byte RotaryLighting = 0x10;
    public const byte RotaryTerminator = 0xFA; // byte 3 of a non-combo code

    public const byte RotaryModeFirmware = 0x10;
    public const byte RotaryModeSoftware = 0x11;

    /// <summary>
    /// Rotary functions offered to the panel. Names are the on-the-wire contract
    /// with nexus-web's KeebRotaryView; each maps to a code via
    /// <see cref="RotaryFunctionByte"/>. Firmware-mode handles all of these
    /// natively, so no host-side input injection is required.
    /// </summary>
    public static readonly string[] RotaryFunctions =
        { "VolumeAdjustment", "BrightnessAdjustment", "Scale", "AltTab", "CtrlTab", "ScrollX", "ScrollY" };

    /// <summary>Default firmware palette (legacy KeebFwAnimationSetting): a rainbow.</summary>
    private static readonly (byte R, byte G, byte B)[] DefaultPalette =
    {
        (255, 0, 0), (255, 125, 0), (125, 255, 0), (0, 255, 0),
        (0, 255, 125), (0, 125, 255), (0, 0, 255), (125, 0, 255),
    };

    /// <summary>Firmware animation byte. Web FW_EFFECTS order maps to 0x01..0x06.</summary>
    public static byte AnimationModeByte(string? mode) => Norm(mode) switch
    {
        "static" => 0x01,
        "breathe" or "breathing" => 0x02,
        "rainbow" => 0x03,
        "wave" => 0x04,
        "flow" => 0x05,
        "pingpong" => 0x06,
        _ => 0x01,
    };

    /// <summary>Reverse of <see cref="AnimationModeByte"/>: device anim byte → web effect name (null if unknown).</summary>
    public static string? AnimationModeName(byte b) => b switch
    {
        0x01 => "Static",
        0x02 => "Breathe",
        0x03 => "Rainbow",
        0x04 => "Wave",
        0x05 => "Flow",
        0x06 => "PingPong",
        _ => null,
    };

    /// <summary>
    /// Device brightness byte (0-255) → 0-100% slider. Inverse of the page[4]
    /// encode in <see cref="BuildSettingsPage"/>, so a value Nexus wrote round-trips
    /// and a knob-set value maps to the nearest percent.
    /// </summary>
    public static int BrightnessPercentFromByte(byte b) => (int)Math.Round(b / 255.0 * 100);

    /// <summary>Speed byte: 1 = fastest, 5 = slowest. Web FW_SPEEDS = Slow..Rapid.</summary>
    public static byte SpeedByte(string? speed) => Norm(speed) switch
    {
        "rapid" => 1,
        "energetic" => 2,
        "standard" or "medium" => 3,
        "laidback" => 4,
        "slow" => 5,
        _ => 3,
    };

    /// <summary>Direction byte (0..3). Web DIRECTIONS values map directly.</summary>
    public static byte DirectionByte(string? dir) => Norm(dir) switch
    {
        "lefttoright" or "forward" => 0x00,
        "righttoleft" or "reverse" => 0x01,
        "toptobottom" => 0x02,
        "bottomtotop" => 0x03,
        _ => 0x00,
    };

    public static byte RotaryFunctionByte(string? fn) => Norm(fn) switch
    {
        "volumeadjustment" or "volume" => RotaryVolume,
        "brightnessadjustment" or "brightness" => RotaryBrightness,
        "scale" or "scaling" => RotaryScale,
        "alttab" => RotaryAltTab,
        "ctrltab" => RotaryCtrlTab,
        "scrollx" or "horizontalscroll" => RotaryScrollX,
        "scrolly" or "verticalscroll" => RotaryScrollY,
        "lighting" or "lightingmovement" or "waveadjustment" => RotaryLighting,
        _ => RotaryVolume,
    };

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static void WriteRotaryCode(byte[] page, int at, string? fn)
    {
        page[at + 0] = RotaryFunctionByte(fn);
        page[at + 1] = 0x00;
        page[at + 2] = 0x00;
        page[at + 3] = RotaryTerminator;
    }

    /// <summary>
    /// Build the 65-byte settings page (byte 0 = report id 0x00) for the 0x06
    /// write from the persisted desired state.
    /// </summary>
    public static byte[] BuildSettingsPage(KeebSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var page = new byte[KeebLayout.PageSize]; // 65; [0] = report id 0x00
        page[1] = 0x01; // debounce (reserved; legacy sends 1)

        // Game-mode bitfield. Bit 4 = LED master ON - must stay set or the board
        // goes dark. (1 = key disabled, matching the firmware spec semantics.)
        byte gm = 0;
        if (s.GameMode.WindowsKey) gm |= 1 << 0;
        if (s.GameMode.ShiftTab) gm |= 1 << 1;
        if (s.GameMode.AltF4) gm |= 1 << 2;
        if (s.GameMode.AltTab) gm |= 1 << 3;
        gm |= 1 << 4; // LED on
        page[2] = gm;

        var anim = AnimationModeByte(s.FirmwareLighting.AnimationMode);
        page[3] = anim;
        // Brightness (page[4] = doc byte 3, range 0-255). The firmware applies this
        // to the running animation LIVE - verified on the bench: the rotary knob's
        // brightness function moves exactly this byte, and a plain host write of it
        // dims the animation immediately (no mode re-init, no palette trick needed).
        // Map the 0-100% slider linearly to 0-255, as the legacy KeebSettings did.
        page[4] = (byte)Math.Round(Math.Clamp(s.FirmwareLighting.Brightness, 0, 100) / 100.0 * 255);
        page[5] = SpeedByte(s.FirmwareLighting.Speed);
        // Color index: Static shows one palette colour (index 0); animated modes
        // use index 8 (the 8-colour palette as a gradient).
        page[6] = anim == 0x01 ? (byte)0 : (byte)8;
        page[7] = DirectionByte(s.FirmwareLighting.Direction);

        // Firmware colour palette (full-intensity rainbow; byte 4 handles dimming).
        for (var i = 0; i < 8; i++)
        {
            var c = DefaultPalette[i];
            page[12 + i * 3] = c.R;
            page[13 + i * 3] = c.G;
            page[14 + i * 3] = c.B;
        }

        // Rotary: firmware mode handles volume/brightness/etc. natively. Right
        // encoder at 37..40, left at 41..44 (legacy ScrollWheel byte order).
        page[36] = RotaryModeFirmware;
        WriteRotaryCode(page, 37, s.RotaryRight);
        WriteRotaryCode(page, 41, s.RotaryLeft);

        return page;
    }
}
