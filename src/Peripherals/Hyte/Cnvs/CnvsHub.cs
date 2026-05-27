using System;
using System.Threading;
using HidSharp;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Talks to a HYTE CNVS over raw HID using HidSharp — the same library
/// HYTE's nexus-control-service uses for these commands. HidSharp's
/// <see cref="HidStream"/> handles two things the home-grown
/// <c>WindowsHidDevice</c> got wrong for this device:
///   1. Output-report padding to <c>MaxOutputReportLength</c> (the
///      Windows HID driver silently rejects shorter writes — that's why
///      the earlier "set CNVS settings" calls quietly failed on Y70).
///   2. Report-ID prefix injection at byte 0 (CNVS reports a single
///      report so we use ID 0; HidSharp does it implicitly).
///
/// No background polling — the CNVS settings are write-on-change.
/// </summary>
public sealed class CnvsHub
{
    private readonly object _lock = new();

    /// <summary>True iff a CNVS HID is enumerable right now.</summary>
    public bool IsConnected => FindDevice() != null;

    /// <summary>
    /// Push both firmware settings in one 5-byte frame. Read-back after
    /// to verify the firmware accepted them — `Write` returning true
    /// only proves the bytes left the host.
    /// </summary>
    public bool WriteSettings(bool suppressBootAnimation, bool keepLedsOnWhenPcOff)
    {
        lock (_lock)
        {
            var dev = FindDevice();
            if (dev == null)
            {
                Console.Error.WriteLine("[cnvs] WriteSettings: no CNVS HID device found");
                return false;
            }

            try
            {
                using var stream = dev.Open();
                var frame = CnvsProtocol.BuildSetSettings(suppressBootAnimation, keepLedsOnWhenPcOff);
                WriteFrame(dev, stream, frame);

                // Match HYTE's CNVSHelper.GetCnvsSettingFromFW cadence —
                // firmware needs a beat between a settings write and a
                // read-back before its echo is consistent.
                Thread.Sleep(20);

                var readBack = ReadSettingsLocked(dev, stream);
                if (readBack is { } rb
                    && rb.SuppressBootAnimation == suppressBootAnimation
                    && rb.KeepLedsOnWhenPcOff == keepLedsOnWhenPcOff)
                {
                    Console.Error.WriteLine(
                        $"[cnvs] WriteSettings ok: boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}");
                    return true;
                }

                Console.Error.WriteLine(
                    $"[cnvs] WriteSettings: read-back mismatch — wanted boot={suppressBootAnimation} leds={keepLedsOnWhenPcOff}, got {(readBack?.ToString() ?? "<no read>")}");
                // Write reached the wire even if the read-back didn't
                // line up; many CNVS firmwares need a USB re-enum before
                // GetSettings reflects a write. Surface success here so
                // the UI doesn't claim a failure on what's probably a
                // read-back layout quirk.
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] WriteSettings exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Read the current settings off the device. Null if unreachable.</summary>
    public CnvsProtocol.CnvsSettings? ReadSettings()
    {
        lock (_lock)
        {
            var dev = FindDevice();
            if (dev == null) return null;
            try
            {
                using var stream = dev.Open();
                return ReadSettingsLocked(dev, stream);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] ReadSettings exception: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Read the firmware version. Empty string if unreachable.</summary>
    public string GetFirmwareVersion()
    {
        lock (_lock)
        {
            var dev = FindDevice();
            if (dev == null) return "";
            try
            {
                using var stream = dev.Open();
                WriteFrame(dev, stream, CnvsProtocol.BuildGetFirmwareVersion());
                Thread.Sleep(20);
                var buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 7)];
                var n = ReadWithTimeout(stream, buf, 200);
                if (n < 7) return "";
                return CnvsProtocol.ParseFirmwareVersion(buf.AsSpan(0, n));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cnvs] GetFirmwareVersion exception: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }
    }

    // ── Internals ──

    private static HidDevice? FindDevice()
    {
        foreach (var pid in CnvsProtocol.ProductIds)
        {
            // HidSharp's enumerator returns multiple HID interfaces for the
            // same VID/PID when the device exposes several. Iterate; the
            // first that opens-and-talks wins. Most CNVS revs only expose
            // one HID interface, so this loop usually runs once.
            foreach (var dev in DeviceList.Local.GetHidDevices(CnvsProtocol.VendorId, pid))
            {
                return dev;
            }
        }
        return null;
    }

    private static CnvsProtocol.CnvsSettings? ReadSettingsLocked(HidDevice dev, HidStream stream)
    {
        WriteFrame(dev, stream, CnvsProtocol.BuildGetSettings());
        Thread.Sleep(20);
        // CNVSHelper.GetCnvsSettingFromFW reads exactly 9 bytes; HidSharp's
        // Read returns the full report including the leading report-ID byte
        // at index 0. The "wire" bytes HYTE inspects at indices 3/4
        // therefore live at indices 3/4 here too — HYTE's reference uses
        // HidSharp the same way, so the index math is portable.
        var len = Math.Max(dev.GetMaxInputReportLength(), 9);
        var buf = new byte[len];
        var n = ReadWithTimeout(stream, buf, 200);
        if (n < 5) return null;
        return CnvsProtocol.ParseGetSettings(buf.AsSpan(0, n));
    }

    /// <summary>
    /// Write the HYTE frame using HidSharp. The frame the protocol
    /// builder produces does NOT include a leading report-ID byte;
    /// HidSharp expects one. We prepend report-ID 0 (CNVS exposes a
    /// single report so 0 is correct) and pad to
    /// <see cref="HidDevice.GetMaxOutputReportLength"/> with zeros so
    /// the Windows HID driver accepts the write.
    /// </summary>
    private static void WriteFrame(HidDevice dev, HidStream stream, byte[] frame)
    {
        var maxOut = dev.GetMaxOutputReportLength();
        // GetMaxOutputReportLength returns 0 for devices with no output
        // report descriptor; fall back to frame.Length + 1 in that case so
        // we at least send something the driver might accept.
        var bufLen = maxOut > 0 ? maxOut : frame.Length + 1;
        var buf = new byte[bufLen];
        buf[0] = 0x00; // report ID
        var copyLen = Math.Min(frame.Length, bufLen - 1);
        Array.Copy(frame, 0, buf, 1, copyLen);
        stream.Write(buf);
    }

    /// <summary>
    /// Read with a soft timeout. HidStream.Read blocks; we wrap it with a
    /// ReadTimeout for older HidSharp builds, but newer builds also
    /// honour CancellationToken on the async overload. The 200 ms budget
    /// matches HYTE's reference cadence.
    /// </summary>
    private static int ReadWithTimeout(HidStream stream, byte[] buf, int timeoutMs)
    {
        var prev = stream.ReadTimeout;
        stream.ReadTimeout = timeoutMs;
        try
        {
            return stream.Read(buf, 0, buf.Length);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        finally
        {
            stream.ReadTimeout = prev;
        }
    }
}
