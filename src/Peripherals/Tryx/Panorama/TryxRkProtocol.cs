using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>One overlay stat line: the raw stat label and its formatted value string.</summary>
public readonly record struct TryxOverlayLine(string Label, string Value);

/// <summary>
/// Hand-rolled protobuf writer for the RK-firmware Panorama (VID 0x391A, "RK PANO").
/// A frame is ASCII "TRYX" followed by a little-endian uint32 payload length, followed
/// by the protobuf body; tags are (fieldNumber &lt;&lt; 3) | wireType varints. Camera-verified
/// against the physical panel. The heartbeat, brightness, and overlay layout commands
/// are decoded; the rest of the schema is unknown, so this class exposes nothing else.
/// </summary>
public static class TryxRkProtocol
{
    private static readonly byte[] FrameMagic = Encoding.ASCII.GetBytes("TRYX");

    // Camera-verified: widget f7=19 and font "roboto-regular" are required-present.
    // Widget f6 selects the text's horizontal alignment WITHIN the widget box:
    // 1 = left, 2 = center, 3 = right (Kanali's own sysinfo overlay uses 1/left).
    private const int OverlayWidgetF7 = 19;
    private const string OverlayFontName = "roboto-regular";
    private const int OverlayAlignLeftF6 = 1;
    private const int OverlayAlignCenterF6 = 2;
    private const int OverlayAlignRightF6 = 3;

    // Widgets span the full content width; f6 places the text left/center/right
    // within it. Coordinate space is approx 2240x1080 (camera-verified). The stat
    // block is centered vertically for any 1/2/3 line count.
    private const int OverlayInsetX = 60;
    private const int OverlayContentWidth = 2120;
    private const int OverlayPanelHeight = 1080;
    private const int OverlayLineStepY = 250;
    private const int OverlayValueLabelOffsetY = 150;
    private const int OverlayValueWidgetHeight = 220;
    private const int OverlayLabelWidgetHeight = 160;
    private const int OverlayValueFontSize = 130;
    private const int OverlayLabelFontSize = 52;

    /// <summary>
    /// Session keep-alive. The panel drops the screen to standby after about 10
    /// seconds without one, so the caller must resend on roughly a 1 Hz cadence.
    /// Field 1 is an empty submessage; field 10 carries a fixed "hello?" string.
    /// </summary>
    public static byte[] BuildHeartbeat()
    {
        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());

        var greeting = new List<byte>();
        WriteLengthDelimited(greeting, fieldNumber: 1, Encoding.ASCII.GetBytes("hello?"));
        WriteLengthDelimited(payload, fieldNumber: 10, greeting.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>
    /// Minimal screen + brightness write, camera-verified not to disturb the
    /// currently playing media or any other panel state. Field 200 nests field 5:
    /// field 1 = screen-enable (present/1 = on; omitted = screen off/black), field
    /// 2 = brightness percent. Setting brightness carries the current screen state;
    /// toggling the screen carries the current brightness.
    /// </summary>
    public static byte[] BuildConfig(bool screenOn, int brightnessPercent)
    {
        var clamped = Math.Clamp(brightnessPercent, 0, 100);

        var selector = new List<byte>();
        if (screenOn)
        {
            WriteVarintField(selector, fieldNumber: 1, 1);
        }
        WriteVarintField(selector, fieldNumber: 2, (ulong)clamped);

        var configBlock = new List<byte>();
        WriteLengthDelimited(configBlock, fieldNumber: 5, selector.ToArray());

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 200, configBlock.ToArray());

        return WrapFrame(payload);
    }

    // The panel's built-in wallpapers are named default_NN.mp4.h264_2240x1080; the
    // power-on and standby clips are fixed. Camera/capture-verified: switching a
    // preset is a field 200 config where f1=power-on media, f2=standby media (f1=1
    // + name), f3=the active wallpaper (nested f3=name), f5=screen+brightness.
    private const string PresetPowerOnMedia = "default_poweron.mp4.h264_2240x1080";
    private const string PresetStandbyMedia = "default_standby.mp4.h264_2240x1080";

    /// <summary>
    /// Media filename for the 1-based preset index, e.g. 2 -> the string the panel
    /// stores for its second built-in wallpaper.
    /// </summary>
    public static string PresetMediaFile(int presetNumber)
        => $"default_{presetNumber:D2}.mp4.h264_2240x1080";

    /// <summary>
    /// Selects a built-in wallpaper. <paramref name="wallpaperMedia"/> is the active
    /// clip (see <see cref="PresetMediaFile"/>); screen state and brightness ride
    /// along in the same config so the panel keeps them.
    /// </summary>
    public static byte[] BuildPreset(string wallpaperMedia, bool screenOn, int brightnessPercent)
    {
        var clamped = Math.Clamp(brightnessPercent, 0, 100);

        var powerOn = new List<byte>();
        WriteLengthDelimited(powerOn, fieldNumber: 1, Encoding.UTF8.GetBytes(PresetPowerOnMedia));

        var standby = new List<byte>();
        WriteVarintField(standby, fieldNumber: 1, 1);
        WriteLengthDelimited(standby, fieldNumber: 2, Encoding.UTF8.GetBytes(PresetStandbyMedia));

        var wallpaper = new List<byte>();
        WriteLengthDelimited(wallpaper, fieldNumber: 3, Encoding.UTF8.GetBytes(wallpaperMedia));

        var selector = new List<byte>();
        if (screenOn)
        {
            WriteVarintField(selector, fieldNumber: 1, 1);
        }
        WriteVarintField(selector, fieldNumber: 2, (ulong)clamped);

        var configBlock = new List<byte>();
        WriteLengthDelimited(configBlock, fieldNumber: 1, powerOn.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 2, standby.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 3, wallpaper.ToArray());
        WriteLengthDelimited(configBlock, fieldNumber: 5, selector.ToArray());

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 200, configBlock.ToArray());

        return WrapFrame(payload);
    }

    /// <summary>
    /// Sensor/text overlay layout. Field 201 nests a repeated field 1 per widget:
    /// f1=widgetId, f2=x, f3=y, f4=w, f5=h, f6/f7 fixed, then a repeated field 8 per
    /// text element (f1=elemId, f2=1 flag, f8=font, f9=fontSize, f10=RGB color,
    /// f11=text). Each stat line renders as two stacked widgets, a large value and a
    /// small label below it. An empty <paramref name="lines"/> list sends a field 201
    /// with zero widgets, which clears the overlay.
    /// </summary>
    public static byte[] BuildOverlay(IReadOnlyList<TryxOverlayLine> lines, int colorRgb, string align)
    {
        var alignF6 = align switch
        {
            "Right" => OverlayAlignRightF6,
            "Center" => OverlayAlignCenterF6,
            _ => OverlayAlignLeftF6,
        };

        // Center the whole stack vertically. A line's value sits at lineY and its
        // label OverlayValueLabelOffsetY below; the block spans the first value to
        // the last label plus the label height.
        var blockHeight = (lines.Count - 1) * OverlayLineStepY
            + OverlayValueLabelOffsetY + OverlayLabelWidgetHeight;
        var firstLineY = Math.Max(0, (OverlayPanelHeight - blockHeight) / 2);

        var f201Body = new List<byte>();
        for (var i = 0; i < lines.Count; i++)
        {
            var lineY = firstLineY + i * OverlayLineStepY;
            var widgetIdBase = i * 2;
            AppendOverlayWidget(
                f201Body, widgetIdBase, OverlayInsetX, lineY,
                OverlayContentWidth, OverlayValueWidgetHeight, alignF6,
                OverlayValueFontSize, colorRgb, lines[i].Value);
            AppendOverlayWidget(
                f201Body, widgetIdBase + 1, OverlayInsetX, lineY + OverlayValueLabelOffsetY,
                OverlayContentWidth, OverlayLabelWidgetHeight, alignF6,
                OverlayLabelFontSize, colorRgb, lines[i].Label);
        }

        var payload = new List<byte>();
        WriteLengthDelimited(payload, fieldNumber: 1, Array.Empty<byte>());
        WriteLengthDelimited(payload, fieldNumber: 201, f201Body.ToArray());

        return WrapFrame(payload);
    }

    private static void AppendOverlayWidget(
        List<byte> f201Body, int widgetId, int x, int y, int w, int h, int alignF6, int fontSize, int colorRgb, string text)
    {
        var elem = new List<byte>();
        WriteVarintField(elem, fieldNumber: 1, (ulong)widgetId);
        WriteVarintField(elem, fieldNumber: 2, 1);
        WriteLengthDelimited(elem, fieldNumber: 8, Encoding.UTF8.GetBytes(OverlayFontName));
        WriteVarintField(elem, fieldNumber: 9, (ulong)fontSize);
        WriteVarintField(elem, fieldNumber: 10, (ulong)colorRgb);
        WriteLengthDelimited(elem, fieldNumber: 11, Encoding.UTF8.GetBytes(text));

        var widget = new List<byte>();
        WriteVarintField(widget, fieldNumber: 1, (ulong)widgetId);
        WriteVarintField(widget, fieldNumber: 2, (ulong)x);
        WriteVarintField(widget, fieldNumber: 3, (ulong)y);
        WriteVarintField(widget, fieldNumber: 4, (ulong)w);
        WriteVarintField(widget, fieldNumber: 5, (ulong)h);
        WriteVarintField(widget, fieldNumber: 6, (ulong)alignF6);
        WriteVarintField(widget, fieldNumber: 7, OverlayWidgetF7);
        WriteLengthDelimited(widget, fieldNumber: 8, elem.ToArray());

        WriteLengthDelimited(f201Body, fieldNumber: 1, widget.ToArray());
    }

    private static byte[] WrapFrame(List<byte> payload)
    {
        var frame = new byte[FrameMagic.Length + 4 + payload.Count];
        FrameMagic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(FrameMagic.Length, 4), (uint)payload.Count);
        payload.CopyTo(frame, FrameMagic.Length + 4);
        return frame;
    }

    private static void WriteVarintField(List<byte> buf, int fieldNumber, ulong value)
    {
        WriteTag(buf, fieldNumber, wireType: 0);
        WriteVarint(buf, value);
    }

    private static void WriteLengthDelimited(List<byte> buf, int fieldNumber, byte[] value)
    {
        WriteTag(buf, fieldNumber, wireType: 2);
        WriteVarint(buf, (ulong)value.Length);
        buf.AddRange(value);
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType)
        => WriteVarint(buf, ((ulong)(uint)fieldNumber << 3) | (uint)wireType);

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }
}
