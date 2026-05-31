using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Singleton coordinator for the HYTE Keeb TKL. Owns the open vendor-HID
/// interface and the high-level read/write operations the lighting provider,
/// REST routes, and input worker call into. Mirrors <see cref="Np50Hub"/>,
/// swapping the serial transport for the raw-HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>): feature reports
/// arm a stream and 65-byte output reports carry the RGB pages.
///
/// We drive the keyboard directly instead of via OpenRGB — the bundled
/// openrgb-headless has its "HYTE Keeb TKL" detector disabled
/// (<see cref="Rgb.OpenRgbProcessManager"/>) so nothing else holds the
/// interface. Hot-plug is self-healing: each <see cref="EnsureConnected"/>
/// re-enumerates.
/// </summary>
public sealed class KeebHub : IDisposable
{
    /// <summary>User-facing product label, shared by every page that surfaces the keeb.</summary>
    public const string ProductName = "HYTE Keeb TKL";

    private readonly IHidEnumerator _hid;
    private readonly object _lock = new();
    private IHidDevice? _device;
    private bool _disposed;

    // Reusable wire buffers (avoid per-frame allocation at 30 Hz).
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

    /// <summary>
    /// Open the keeb's vendor HID interface if not already open. Picks the
    /// collection whose usage page/usage match the protocol's vendor pair,
    /// falling back to a collection with feature + 65-byte output reports.
    /// </summary>
    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (_device is not null) return true;
        lock (_lock)
        {
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
    }

    public void Disconnect()
    {
        lock (_lock)
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
        KeebLayout.MapKeysToWire(keysInLedOrder, _keyWire);
        return StreamZone(KeebProtocol.KeyboardStreamFeature, _keyWire, KeebLayout.KeyPageCount);
    }

    /// <summary>Stream an underglow-zone frame (63 LEDs, 3 pages).</summary>
    public bool WriteSurround(ReadOnlySpan<RgbColor> ledsInLedOrder)
    {
        Array.Clear(_surroundWire);
        var n = Math.Min(ledsInLedOrder.Length, _surroundWire.Length);
        for (var i = 0; i < n; i++) _surroundWire[i] = ledsInLedOrder[i];
        return StreamZone(KeebProtocol.SurroundStreamFeature, _surroundWire, KeebLayout.SurroundPageCount);
    }

    private bool StreamZone(byte[] feature, RgbColor[] wire, int pageCount)
    {
        if (!EnsureConnected()) return false;
        var dev = _device;
        if (dev is null) return false;
        try
        {
            // Arm the stream, then push each 65-byte page as one output report
            // (mirrors OpenRGB's hid_send_feature_report + N×hid_write).
            if (!dev.SetFeature(feature)) return RecordWriteFailure("feature");
            var pages = KeebProtocol.BuildStreamPages(wire, pageCount);
            for (var p = 0; p < pageCount; p++)
            {
                if (!dev.Write(pages.AsSpan(p * KeebLayout.PageSize, KeebLayout.PageSize)))
                    return RecordWriteFailure("page");
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
            return RecordWriteFailure(ex.GetType().Name);
        }
    }

    // A single dropped report is transient (USB jitter); only tear down the
    // interface after a sustained burst so the next tick re-enumerates.
    private bool RecordWriteFailure(string where)
    {
        var n = Interlocked.Increment(ref _consecutiveWriteFailures);
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            Console.Error.WriteLine($"[keeb] {n} consecutive write failures ({where}) — dropping interface");
            _consecutiveWriteFailures = 0;
            Disconnect();
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
