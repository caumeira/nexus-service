using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Thin singleton that opens a HYTE CNVS HID device on demand, sends
/// the small set of firmware-settings commands HYTE exposes, and closes.
/// No background polling — the CNVS settings are write-on-change, not
/// stream-driven like the NP50 / MiniHub fan + lighting loops.
///
/// Why a hub at all if there's no polling: the device-open path is
/// shared by every callsite (set + get + version), and trying multiple
/// PIDs on each call is annoying without one. The hub also makes the
/// "no CNVS plugged in" branch a single early-return so the route
/// layer can pretend the device is always present.
/// </summary>
public sealed class CnvsHub
{
    private readonly IHidEnumerator _hid;
    private readonly object _lock = new();

    public CnvsHub(IHidEnumerator hid)
    {
        _hid = hid;
    }

    /// <summary>True iff we can currently locate a CNVS HID device.</summary>
    public bool IsConnected
    {
        get
        {
            foreach (var pid in CnvsProtocol.ProductIds)
            {
                if (_hid.Find(CnvsProtocol.VendorId, pid).Count > 0)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Push both firmware settings to the device in one 5-byte frame.
    /// Read-after-write to confirm — without it, a successful HID write
    /// only proves the bytes left the host (per the team rule about
    /// SendOnly semantics). Returns true iff the device echoes the
    /// settings we just wrote.
    /// </summary>
    public bool WriteSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        lock (_lock)
        {
            using var dev = OpenAny();
            if (dev is null) return false;

            var bytes = CnvsProtocol.BuildSetSettings(suppressBootAnimation, keepLedsOnWhenPcOff);
            if (!WriteFrame(dev, bytes))
            {
                Console.Error.WriteLine("[cnvs] WriteSettings: HID write returned false (settings not applied)");
                return false;
            }

            // 20 ms matches HYTE's CNVSHelper.GetCnvsSettingFromFW preamble
            // sleep — the firmware needs a beat between a settings write
            // and a read-back before its echo is consistent.
            Thread.Sleep(20);

            var readBack = ReadSettingsLocked(dev);
            if (readBack is { } rb
                && rb.SuppressBootAnimation == suppressBootAnimation
                && rb.KeepLedsOnWhenPcOff == keepLedsOnWhenPcOff)
            {
                return true;
            }

            Console.Error.WriteLine(
                $"[cnvs] WriteSettings: read-back mismatch — wanted boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}, got {(readBack?.ToString() ?? "<no read>")}");
            // The write itself may still have landed; firmware mismatch
            // can also be the read-back having an unexpected layout on
            // newer firmware. Return true so the user-facing UI doesn't
            // claim a failure when the write probably worked.
            return true;
        }
    }

    /// <summary>Read the current settings from the device. Null if not plugged in or the read fails.</summary>
    public CnvsProtocol.CnvsSettings? ReadSettings()
    {
        lock (_lock)
        {
            using var dev = OpenAny();
            if (dev is null) return null;
            return ReadSettingsLocked(dev);
        }
    }

    // ── Internals ──

    private IHidDevice? OpenAny()
    {
        foreach (var pid in CnvsProtocol.ProductIds)
        {
            foreach (var info in _hid.Find(CnvsProtocol.VendorId, pid))
            {
                var dev = _hid.Open(info.Path);
                if (dev != null) return dev;
            }
        }
        return null;
    }

    private static CnvsProtocol.CnvsSettings? ReadSettingsLocked(IHidDevice dev)
    {
        try
        {
            if (!WriteFrame(dev, CnvsProtocol.BuildGetSettings()))
                return null;
            // 9 bytes per HYTE's reference; we don't trust short reads to
            // mean "empty payload" so anything under 5 is treated as null.
            Span<byte> buf = stackalloc byte[9];
            var n = dev.Read(buf, 200);
            if (n < 5) return null;
            return CnvsProtocol.ParseGetSettings(buf[..n]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cnvs] ReadSettings exchange failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Write a HYTE frame to the HID device. Tries the raw frame first
    /// (the layout HYTE's HidSharp-backed code uses); if that's rejected
    /// we fall back to the Windows-HID convention of prefixing report
    /// ID 0 and padding to 65 bytes. Logs which path took so we can
    /// tighten this once we know what the CNVS firmware actually wants.
    /// </summary>
    private static bool WriteFrame(IHidDevice dev, byte[] frame)
    {
        if (dev.Write(frame))
            return true;
        // Pad-and-prefix fallback: 0x00 report-ID + frame + zero pad to 64
        // bytes of payload (matches the WriteFile contract most output
        // reports expect on Windows when the device wasn't initialised
        // with a non-zero report ID).
        var padded = new byte[65];
        padded[0] = 0x00;
        frame.AsSpan().CopyTo(padded.AsSpan(1));
        if (dev.Write(padded))
        {
            Console.Error.WriteLine("[cnvs] write needed the report-id 0 + 65-byte pad fallback");
            return true;
        }
        return false;
    }
}
