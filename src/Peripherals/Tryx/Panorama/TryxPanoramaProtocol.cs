using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Frame builder for the Tryx Panorama AIO CDC-ACM serial protocol.
/// Frame encoding: 0x5A | LEN_HI | LEN_LO | (stuffed body) | CRC | 0x5A
/// LEN = bodyLen + 5, big-endian. CRC = sum(LEN_HI, LEN_LO, body...) &amp; 0xFF.
/// Byte stuffing applied to [LEN_HI..CRC]: 0x5A->0x5B 0x01, 0x5B->0x5B 0x02.
/// </summary>
public static class TryxPanoramaProtocol
{
    // Legacy serial+adb firmware id (Google's shared accessory+ADB composite),
    // still used by the CDC-serial hub/discovery in this namespace. Retired for
    // device recognition (see the handler): it is Google's VID, so other Android
    // AIO screens like Deepcool's also present it and read as a false Tryx.
    public const int VendorId = 0x18D1;
    public const int ProductIdPanorama = 0x2D03;

    // Current firmware: a Rockchip AIO screen, Tryx-unique vendor id. Windows binds
    // its default Class-07 descriptor to usbprint.inf (enumerates as "RK PANO");
    // control is raw USB bulk (libusbK) against the sibling 391A:0006 node, not
    // serial/adb, so the CDC-serial hub does not reach it.
    public const int VendorIdRk = 0x391A;
    public const int ProductIdPanoramaRk = 0x1011;

    public static readonly int[] KnownProductIds = { ProductIdPanorama };

    // Incremented atomically for every frame sent to the device.
    private static int _seqNumber = 0;

    // Default smart fan curve from nx_e2e.ps1.
    private static readonly int[][] DefaultSmartCurve =
    {
        new[] { 0, 10 }, new[] { 10, 20 }, new[] { 30, 30 }, new[] { 50, 40 },
        new[] { 65, 55 }, new[] { 80, 70 }, new[] { 90, 100 }, new[] { 100, 100 },
    };

    private const string ZeroSensorJson =
        "{\"network\":{\"upload\":0,\"download\":0}," +
        "\"memory\":{\"total\":0,\"used\":0,\"load\":0,\"temperature\":0,\"speed\":0}," +
        "\"cpu\":{\"load\":0,\"usage\":0,\"temperature\":0,\"speedAverage\":0,\"power\":0,\"voltage\":0}," +
        "\"gpu\":{\"load\":0,\"temperature\":0,\"fan\":0,\"speed\":0,\"power\":0,\"voltage\":0}," +
        "\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0}," +
        "\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}]," +
        "\"motherboard\":{\"temperature\":0,\"pchTemperature\":0}," +
        "\"timestamp\":0}";

    public static string GetModelName(int pid)
    {
        return pid switch
        {
            ProductIdPanorama => "Panorama",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Builds a framed command packet per the nx_e2e.ps1 protocol reference.
    /// SeqNumber is a global Interlocked.Increment starting at 1.
    /// </summary>
    public static byte[] BuildFrame(string reqState, string cmdType, string json)
    {
        var seq = Interlocked.Increment(ref _seqNumber);
        var date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var contentLength = Encoding.UTF8.GetByteCount(json);
        var header =
            $"{reqState} {cmdType} 1\r\n" +
            $"ContentType=json\r\n" +
            $"ContentLength={contentLength}\r\n" +
            $"SeqNumber={seq}\r\n" +
            $"AckNumber=-1\r\n" +
            $"Date={date}\r\n" +
            $"\r\n" +
            json;

        var body = Encoding.UTF8.GetBytes(header);
        var bodyLen = body.Length;
        // LEN = bodyLen + 5 (per nx_e2e.ps1)
        var len = bodyLen + 5;
        var lenHi = (byte)((len >> 8) & 0xFF);
        var lenLo = (byte)(len & 0xFF);

        // Pre-stuffing buffer: LEN_HI, LEN_LO, body bytes, CRC
        var pre = new List<byte>(bodyLen + 3) { lenHi, lenLo };
        pre.AddRange(body);

        // CRC = sum(LEN_HI, LEN_LO, body...) & 0xFF
        int crcSum = 0;
        foreach (var b in pre)
        {
            crcSum += b;
        }
        pre.Add((byte)(crcSum & 0xFF));

        // Byte-stuff the pre region (includes LEN_HI, LEN_LO, body, CRC).
        var stuffed = new List<byte>(pre.Count + 4);
        foreach (var b in pre)
        {
            if (b == 0x5A)
            {
                stuffed.Add(0x5B);
                stuffed.Add(0x01);
            }
            else if (b == 0x5B)
            {
                stuffed.Add(0x5B);
                stuffed.Add(0x02);
            }
            else
            {
                stuffed.Add(b);
            }
        }

        var frame = new byte[stuffed.Count + 2];
        frame[0] = 0x5A;
        stuffed.CopyTo(frame, 1);
        frame[frame.Length - 1] = 0x5A;
        return frame;
    }

    // ── Command builders ──

    public static byte[] BuildConn()
        => BuildFrame("POST", "conn", "{}");

    public static byte[] BuildStateAll(string? sensorJson = null)
        => BuildFrame("STATE", "all", sensorJson ?? ZeroSensorJson);

    public static byte[] BuildTransport(long fileSize, string fileName)
    {
        var json = $"{{\"type\":\"media\",\"fileSize\":{fileSize},\"fileName\":\"{EscapeJson(fileName)}\"}}";
        return BuildFrame("POST", "transport", json);
    }

    public static byte[] BuildTransported(string md5, string fileName)
    {
        var json = $"{{\"md5\":\"{EscapeJson(md5)}\",\"fileName\":\"{EscapeJson(fileName)}\"}}";
        return BuildFrame("POST", "transported", json);
    }

    public static byte[] BuildWaterBlockScreen(bool enable)
    {
        var json = $"{{\"enable\":{(enable ? "true" : "false")}}}";
        return BuildFrame("POST", "waterBlockScreen", json);
    }

    public static byte[] BuildConfigCustom(int brightness, string mediaFileName, TryxOverlayConfig overlay, int[][]? fanSmartMode = null)
        => BuildConfig(brightness,
            BuildIdObject("Customization", mediaFileName, overlay),
            BuildFanLcd("Smart Mode", fanSmartMode ?? DefaultSmartCurve, 40));

    public static byte[] BuildConfigPreset(int brightness, string presetId, TryxOverlayConfig overlay, int[][]? fanSmartMode = null)
        => BuildConfig(brightness,
            BuildIdObject(presetId, null, overlay),
            BuildFanLcd("Smart Mode", fanSmartMode ?? DefaultSmartCurve, 40));

    public static byte[] BuildConfigFanFixed(int brightness, string currentId, bool isCustom, TryxOverlayConfig overlay, int fixedPercent)
        => BuildConfig(brightness,
            isCustom ? BuildIdObject("Customization", currentId, overlay) : BuildIdObject(currentId, null, overlay),
            BuildFanLcd("Fixed Mode", DefaultSmartCurve, fixedPercent));

    // waterBlockScreen.id is ALWAYS the nested object the home UI expects; a bare
    // string is silently ignored (preset selection failed until this was nested).
    // media = the pcMedia filename for "Customization", empty for a preset (the
    // device maps the preset name to its own bundled clip).
    private static string BuildIdObject(string id, string? mediaFileName, TryxOverlayConfig overlay)
    {
        var media = mediaFileName is null ? "[]" : "[\"" + EscapeJson(mediaFileName) + "\"]";
        var filterValue = overlay.Filter is null ? "null" : "\"" + EscapeJson(overlay.Filter) + "\"";
        var settings = "{\"color\":\"" + EscapeJson(overlay.Color) + "\",\"align\":\"" + EscapeJson(overlay.Align) +
                       "\",\"filter\":{\"value\":" + filterValue + ",\"opacity\":" + overlay.Opacity + "},\"badges\":[]}";
        var sysinfoDisplay = BuildSysinfoDisplay(overlay.Stats);
        return "{\"id\":\"" + EscapeJson(id) + "\",\"screenMode\":\"Full Screen\",\"playMode\":\"Single\"," +
               "\"ratio\":\"2:1\",\"media\":" + media +
               ",\"settings\":" + settings +
               ",\"sysinfoDisplay\":" + sysinfoDisplay + "}";
    }

    private static string BuildSysinfoDisplay(string[] stats)
    {
        if (stats.Length == 0)
        {
            return "[]";
        }
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < stats.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append('"').Append(EscapeJson(stats[i])).Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string BuildFanLcd(string mode, int[][] smartCurve, int fixedPercent)
        => "{\"mode\":\"" + mode + "\",\"smartMode\":" + BuildFanCurveJson(smartCurve) + ",\"fixedMode\":" + fixedPercent + "}";

    private static byte[] BuildConfig(int brightness, string idObjectJson, string fanLcdJson)
    {
        var json =
            "{\"temperature\":\"Celsius\"," +
            "\"waterBlockScreen\":{" +
            $"\"enable\":true,\"displayInSleep\":false,\"brightness\":{brightness}," +
            "\"rotate\":270," +
            "\"id\":" + idObjectJson + "," +
            "\"fanLCD\":" + fanLcdJson + "}," +
            "\"spec\":{\"cpu\":\"Nexus\",\"gpu\":\"Nexus\"}," +
            "\"turboPump\":false}";
        return BuildFrame("POST", "config", json);
    }

    // ── Helpers ──

    private static string BuildFanCurveJson(int[][] curve)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < curve.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append('[').Append(curve[i][0]).Append(',').Append(curve[i][1]).Append(']');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
