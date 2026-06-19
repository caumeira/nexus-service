using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Singleton coordinator for the HYTE Keeb TKL. Owns the open vendor-HID
/// interface and the high-level read/write operations the lighting provider,
/// REST routes, and connection worker call into. Mirrors <see cref="Np50Hub"/>,
/// swapping the serial transport for the raw-HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>): feature reports
/// arm a stream/settings write and 65-byte output reports carry the pages.
///
/// We drive the keyboard directly instead of via OpenRGB - the bundled
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

    /// <summary>Per-side settle around a 0x06 settings write (HYTE reference: "fw will crash otherwise").</summary>
    private const int SettingsWriteSettleMs = 20;

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

        var chosen = FindVendorInterface(_hid);
        if (chosen is null) return false;

        var dev = _hid.Open(chosen.Path);
        if (dev is null)
        {
            ServiceLog.Error($"[keeb] open failed for {chosen.Path}");
            return false;
        }
        _device = dev;
        State.Serial = !string.IsNullOrWhiteSpace(chosen.Serial)
            ? chosen.Serial!
            : StableIdFromPath(chosen.Path);
        _consecutiveWriteFailures = 0;
        ServiceLog.Info($"[keeb] connected (serial={State.Serial}, usage={chosen.UsagePage:X4}/{chosen.Usage:X2})");
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

    /// <summary>
    /// Pick the keeb's vendor protocol collection (usage page 0xFF11 / usage
    /// 0xF0) from the enumerator, falling back to a collection that can carry
    /// the protocol's reports. Shared by the write hub and the input reader so
    /// both target the same HID interface. Returns null when not found.
    /// </summary>
    internal static HidDeviceInfo? FindVendorInterface(IHidEnumerator hid)
    {
        HidDeviceInfo? chosen = null;
        foreach (var info in hid.Find(KeebProtocol.VendorId, KeebProtocol.ProductId))
        {
            if (info.UsagePage == KeebProtocol.VendorUsagePage && info.Usage == KeebProtocol.VendorUsage)
            {
                return info;
            }
            if (chosen is null
                && info.FeatureReportByteLength >= KeebProtocol.FeatureReportSize
                && info.OutputReportByteLength >= KeebLayout.PageSize)
            {
                chosen = info;
            }
        }
        return chosen;
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
                // Settle between arming the settings write and sending the
                // page, and again after. HYTE's reference driver
                // (KeebTKLCommand.SetSettings) waits ~20 ms each side with the
                // note "Need delay otherwise fw will crash". Without it the
                // page is stored but the running animation doesn't re-apply it
                // (e.g. a brightness-only change doesn't dim). Settings writes
                // are rare (user actions).
                if (!dev.SetFeature(KeebProtocol.SettingsWriteFeature)) return RecordWriteFailureLocked("settings-feature");
                System.Threading.Thread.Sleep(SettingsWriteSettleMs);
                if (!dev.Write(page)) return RecordWriteFailureLocked("settings-page");
                System.Threading.Thread.Sleep(SettingsWriteSettleMs);
                _consecutiveWriteFailures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] settings write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordWriteFailureLocked(ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Read the 0x06 settings page back from the device (SetFeature 0x84 0x06,
    /// then read the input report). Returns the raw response, or null. Used to
    /// inspect what the firmware actually stores (e.g. whether the rotary knob
    /// moves the brightness byte) and to sync the panel to live device state.
    /// </summary>
    public byte[]? ReadSettings()
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return null;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.SettingsReadFeature)) return null;
                var buf = new byte[KeebLayout.PageSize];
                var n = dev.Read(buf, 250);
                return n > 0 ? buf.AsSpan(0, n).ToArray() : null;
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] settings read failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Read a layer's raw key-assignment pages (0x84 F2) for the active
    /// profile. Diagnostic only: arms the read and pulls the layer's
    /// output-report pages back. Returns null when disconnected or any page
    /// read times out. Used to reverse-engineer the firmware's slot order on
    /// the bench without ever writing the table.
    /// </summary>
    public byte[]? ReadLayerRaw(int profile, int layer)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return null;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.LayerFeature(KeebProtocol.Read, profile, layer))) return null;
                var pages = new byte[KeebProtocol.LayerPageCount * KeebLayout.PageSize];
                for (var p = 0; p < KeebProtocol.LayerPageCount; p++)
                {
                    var buf = new byte[KeebLayout.PageSize];
                    var n = dev.Read(buf, 250);
                    if (n <= 0)
                    {
                        // Bail mid-burst: late pages could still land on this
                        // handle and a later ReadSettings would consume one as
                        // stale data. Drop the handle; the next op re-opens.
                        ServiceLog.Error($"[keeb] layer read timed out at page {p}; dropping interface");
                        try { _device?.Dispose(); } catch { /* best effort */ }
                        _device = null;
                        return null;
                    }
                    if (n < KeebLayout.PageSize)
                        ServiceLog.Info($"[keeb] layer page {p} short read ({n} bytes)");
                    System.Array.Copy(buf, 0, pages, p * KeebLayout.PageSize, System.Math.Min(n, KeebLayout.PageSize));
                }
                return pages;
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] layer read failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Write a macro's 4 pages (0xF3) for <paramref name="index"/> (0..31).</summary>
    public bool WriteMacro(int index, byte[] pages)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            return WritePagesLocked(KeebProtocol.MacroFeature(KeebProtocol.Write, index), pages, KeebMacroCodec.PageCount);
        }
    }

    /// <summary>Write a layer's 8 key-assignment pages (0xF2) for (profile, layer).</summary>
    public bool WriteLayer(int profile, int layer, byte[] pages)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            return WritePagesLocked(KeebProtocol.LayerFeature(KeebProtocol.Write, profile, layer), pages, KeebProtocol.LayerPageCount);
        }
    }

    /// <summary>Select the active firmware profile (0 or 1) via 0x02.</summary>
    public bool SetProfile(int profile)
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.ProfileSetFeature(profile))) return RecordWriteFailureLocked("profile");
                State.Profile = profile;
                _consecutiveWriteFailures = 0;
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb] profile write failed: {ex.GetType().Name}: {ex.Message}");
                return RecordWriteFailureLocked(ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Best-effort read of the device-info block (0x05) to populate firmware
    /// version + layout. Non-fatal: the keeb's input-report read path is
    /// firmware-dependent, so a failed read just leaves the defaults.
    /// </summary>
    public bool ReadDeviceInfo()
    {
        lock (_io)
        {
            if (!EnsureConnectedLocked()) return false;
            var dev = _device!;
            try
            {
                if (!dev.SetFeature(KeebProtocol.DeviceInfoRequest)) return false;
                var buf = new byte[KeebLayout.PageSize];
                var n = dev.Read(buf, 200);
                if (n <= 0) return false;
                if (KeebProtocol.ParseDeviceInfo(buf.AsSpan(0, n)) is { } di)
                {
                    State.FirmwareVersion = di.FirmwareVersion;
                    State.Layout = di.Layout;
                    return true;
                }
                return false;
            }
            catch { return false; }
        }
    }

    // Arm a feature then push each 65-byte page as one output report. Caller holds _io.
    private bool WritePagesLocked(byte[] feature, byte[] pages, int pageCount)
    {
        var dev = _device!;
        try
        {
            if (!dev.SetFeature(feature)) return RecordWriteFailureLocked("feature");
            for (var p = 0; p < pageCount; p++)
            {
                if (!dev.Write(pages.AsSpan(p * KeebLayout.PageSize, KeebLayout.PageSize)))
                    return RecordWriteFailureLocked("page");
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex)
        {
            ServiceLog.Error($"[keeb] page write failed: {ex.GetType().Name}: {ex.Message}");
            return RecordWriteFailureLocked(ex.GetType().Name);
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
            ServiceLog.Error($"[keeb] stream failed: {ex.GetType().Name}: {ex.Message}");
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
            ServiceLog.Error($"[keeb] {n} consecutive write failures ({where}) - dropping interface");
            _consecutiveWriteFailures = 0;
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
        return false;
    }

    private static string StableIdFromPath(string path)
    {
        // No serial reported - derive a stable short id from the device path
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
