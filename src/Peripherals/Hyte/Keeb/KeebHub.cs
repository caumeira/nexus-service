using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Singleton coordinator for the HYTE Keeb TKL. Owns the open vendor-HID
/// interface and the high-level read/write operations the lighting provider,
/// REST routes, and connection worker call into. Mirrors <see cref="Np50Hub"/>,
/// swapping the serial transport for the raw-HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>): feature reports
/// arm a stream/settings write and 65-byte output reports carry the pages.
///
/// We drive the keyboard directly instead of via OpenRGB — the bundled
/// openrgb-headless has its "HYTE Keeb TKL" detector disabled
/// (<see cref="Rgb.OpenRgbProcessManager"/>) so nothing else holds the
/// interface. Hot-plug is self-healing: each <see cref="EnsureConnected"/>
/// re-enumerates.
///
/// All device IO is serialised on <c>_io</c>: the 30 Hz RGB stream and the
/// (rare, HTTP-thread) settings write must never interleave their multi-report
/// bursts on the shared HID handle, and a teardown must not dispose the handle
/// mid-burst.
/// </summary>
public sealed class KeebHub : IDisposable
{
    /// <summary>User-facing product label, shared by every page that surfaces the keeb.</summary>
    public const string ProductName = "HYTE Keeb TKL";

    private readonly IHidEnumerator _hid;
    private readonly object _io = new();
    private IHidDevice? _device;
    private bool _disposed;

    // Reusable wire buffers (avoid per-frame allocation at 30 Hz). Guarded by _io.
    private readonly RgbColor[] _keyWire = new RgbColor[KeebLayout.KeyWireSlots];
    private readonly RgbColor[] _surroundWire = new RgbColor[KeebLayout.SurroundWireSlots];

    private int _consecutiveWriteFailures;
    private const int ConsecutiveWriteFailureThreshold = 5;

    public KeebHub(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public KeebState State { get; } = new();

    public bool IsConnected => _device is not null;

    /// <summary>"keeb:&lt;serial&gt;" id, or empty when never connected.</summary>
    public string DeviceId => string.IsNullOrEmpty(State.Serial) ? "" : $"keeb:{State.Serial}";

    /// <summary>Open the keeb's vendor HID interface if not already open.</summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (_device is not null) return true;
        lock (_io) return EnsureConnectedLocked();
    }

    // Caller must hold _io.
    private bool EnsureConnectedLocked()
    {
        if (_disposed) return false;
        if (_device is not null) return true;

        HidDeviceInfo? chosen = null;
        foreach (var info in _hid.Find(KeebProtocol.VendorId, KeebProtocol.ProductId))
        {
            if (info.UsagePage == KeebProtocol.VendorUsagePage && info.Usage == KeebProtocol.VendorUsage)
            {
                chosen = info;
                break;
            }
            // Fallback: a collection that can carry the protocol's reports.
            if (chosen is null
                && info.FeatureReportByteLength >= KeebProtocol.FeatureReportSize
                && info.OutputReportByteLength >= KeebLayout.PageSize)
            {
                chosen = info;
            }
        }
        if (chosen is null) return false;

        var dev = _hid.Open(chosen.Path);
        if (dev is null)
        {
            Console.Error.WriteLine($"[keeb] open failed for {chosen.Path}");
            return false;
        }
        _device = dev;
        State.Serial = !string.IsNullOrWhiteSpace(chosen.Serial)
            ? chosen.Serial!
            : StableIdFromPath(chosen.Path);
        _consecutiveWriteFailures = 0;
        Console.Error.WriteLine($"[keeb] connected (serial={State.Serial}, usage={chosen.UsagePage:X4}/{chosen.Usage:X2})");
        return true;
    }

    public void Disconnect()
    {
        lock (_io)
        {
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
    }

    // ── RGB writes ──

    /// <summary>
    /// Stream a keyboard-zone frame. <paramref name="keysInLedOrder"/> carries
    /// one color per physical key in <see cref="KeebLayout.KeyWireValues"/>
    /// order; we scatter them to wire slots, arm the stream, and push 6 pages.
    /// </summary>
    public bool WriteKeyboard(ReadOnlySpan<RgbColor> keysInLedOrder)
    {
        lock (_io)
        {
            KeebLayout.MapKeysToWire(keysInLedOrder, _keyWire);
            return StreamZoneLocked(KeebProtocol.KeyboardStreamFeature, _keyWire, KeebLayout.KeyPageCount);
        }
    }

    /// <summary>Stream an underglow-zone frame (63 LEDs, 3 pages).</summary>
    public bool WriteSurround(ReadOnlySpan<RgbColor> ledsInLedOrder)
    {
        lock (_io)
        {
            Array.Clear(_surroundWire);
            var n = Math.Min(ledsInLedOrder.Length, _surroundWire.Length);
            for (var i = 0; i < n; i++) _surroundWire[i] = ledsInLedOrder[i];
            return StreamZoneLocked(KeebProtocol.SurroundStreamFeature, _surroundWire, KeebLayout.SurroundPageCount);
        }
    }

    /// <summary>
    /// Write the 0x06 settings page (game mode + firmware animation + rotary).
    /// <paramref name="page"/> is the full 65-byte report (byte 0 = report id).
    /// </summary>
    public bool WriteSettings(ReadOnlySpan<byte> page)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.SettingsWriteFeature)) return RecordWriteFailureLocked("settings-feature");
                if (!dev.Write(page)) return RecordWriteFailureLocked("settings-page");
                _consecutiveWriteFailures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[keeb] settings write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordWriteFailureLocked(ex.GetType().Name);
            }
        }
    }

    // Caller must hold _io.
    private bool StreamZoneLocked(byte[] feature, RgbColor[] wire, int pageCount)
    {
        if (!EnsureConnectedLocked()) return false;
        var dev = _device!;
        try
        {
            // Arm the stream, then push each 65-byte page as one output report
            // (mirrors OpenRGB's hid_send_feature_report + N×hid_write).
            if (!dev.SetFeature(feature)) return RecordWriteFailureLocked("feature");
            var pages = KeebProtocol.BuildStreamPages(wire, pageCount);
            for (var p = 0; p < pageCount; p++)
            {
                if (!dev.Write(pages.AsSpan(p * KeebLayout.PageSize, KeebLayout.PageSize)))
                    return RecordWriteFailureLocked("page");
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[keeb] stream failed: {ex.GetType().Name}: {ex.Message}");
            return RecordWriteFailureLocked(ex.GetType().Name);
        }
    }

    // A single dropped report is transient (USB jitter); only tear down the
    // interface after a sustained burst so the next tick re-enumerates.
    // Caller must hold _io; disposes the handle inline (not via Disconnect) so
    // it never needs to re-acquire the lock.
    private bool RecordWriteFailureLocked(string where)
    {
        var n = ++_consecutiveWriteFailures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            Console.Error.WriteLine($"[keeb] {n} consecutive write failures ({where}) — dropping interface");
            _consecutiveWriteFailures = 0;
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
        return false;
    }

    private static string StableIdFromPath(string path)
    {
        // No serial reported — derive a stable short id from the device path
        // so the device id survives reconnects of the same physical port.
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"tkl-{h:x8}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
