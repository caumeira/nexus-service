using System;
using System.Threading;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Peripherals.Y70;

/// <summary>
/// Real <see cref="IY70Provider"/>: drives Y70 brightness + screen power on the
/// panel. Two transports, picked per variant (<see cref="Y70DisplayHub.Variant"/>):
///   • Serial (Touch / Infinite): the STM32 controller's <c>FF CC 01</c> frame
///     over the CDC COM port managed by <see cref="Y70DisplayHub"/>.
///   • DDC/CI (Truly / GW): VCP 0x10 (brightness) + 0xD6 (power) via the
///     platform display-brightness provider, mirroring the reference
///     Y70TouchDdcciDeviceBase: brightness and power are independent writes,
///     and brightness is raw 0-100 (the serial minimum-brightness floor is a
///     PWM humming fix that does not apply to DDC). Truly also exposes a CDC
///     port (fw-version/DFU), so a live serial connection alone must not
///     select the serial transport.
/// Orientation goes through <see cref="IDisplayOrientationProvider"/> (Windows
/// rotation).
///
/// The config store is the source of truth the UI reads; every setter writes
/// the hardware first (best-effort) and then persists, so GET reflects the last
/// requested value even when the panel is briefly detached.
///
/// UNVERIFIED: the DDC/CI branch (Truly / GW) has NOT been tested on hardware;
/// only the serial path (Infinite, RTK0004) was on the bench. The VCP codes and
/// write semantics are ported from the reference Y70DDCCIHelper; confirm on a
/// Truly or GW panel before trusting this path.
/// </summary>
public sealed class Y70Provider : IY70Provider, IDisposable
{
    private readonly Y70DisplayHub _hub;
    private readonly IConfigStore _store;
    private readonly IDisplayOrientationProvider _orientation;
    private readonly IDisplayBrightnessProvider _ddc;

    public Y70Provider(
        Y70DisplayHub hub,
        IConfigStore store,
        IDisplayOrientationProvider orientation,
        IDisplayBrightnessProvider ddc)
    {
        _hub = hub;
        _store = store;
        _orientation = orientation;
        _ddc = ddc;
    }

    public bool IsConnected() => _hub.IsConnected || DdcDisplayId() is not null;

    public string GetOrientation() => _store.Load().Y70.Orientation;

    public void SetOrientation(string orientation)
    {
        _store.Update(s => s.Y70.Orientation = orientation);
    }

    public bool GetForceOrientation() => _store.Load().Y70.ForceOrientation;

    public void SetForceOrientation(bool forceOrientation)
    {
        _store.Update(s => s.Y70.ForceOrientation = forceOrientation);
    }

    /// <summary>Pushes the current effective orientation (PortraitFlipped when
    /// ForceOrientation is set, else the stored preference) to hardware.</summary>
    public void ApplyEffectiveOrientation()
    {
        var y70 = _store.Load().Y70;
        var effective = y70.ForceOrientation ? "PortraitFlipped" : y70.Orientation;
        var (ok, detail) = _orientation.SetY70Orientation(effective);
        // Log every apply (detail distinguishes an actual rotation, "applied X
        // from=Y", from a no-op, "already X") so a re-apply loop is visible.
        ServiceLog.Info($"[y70-orient] effective='{effective}' force={y70.ForceOrientation} ok={ok} detail='{detail}'");
    }

    public int GetBrightness() => _store.Load().Y70.Brightness;

    public void SetBrightness(int brightness)
    {
        var pct = Math.Clamp(brightness, 0, 100);
        if (SerialTransportSelected) ApplySerial(!_store.Load().Y70.ScreenOff, pct);
        else DdcSetBrightnessCoalesced(pct);
        _store.Update(s => s.Y70.Brightness = pct);
    }

    public bool GetToggle() => _store.Load().Y70.ScreenOff;

    public void SetToggle(bool screenOff)
    {
        if (SerialTransportSelected) ApplySerial(!screenOff, _store.Load().Y70.Brightness);
        else DdcSetPower(!screenOff);
        _store.Update(s => s.Y70.ScreenOff = screenOff);
    }

    public bool IsRotated()
    {
        var o = GetOrientation();
        return o == "Portrait" || o == "PortraitFlipped";
    }

    /// <summary>
    /// True when the serial FF CC transport drives this panel. A Truly
    /// enumerates the same CDC port as the serial models (the shared FF DD
    /// version query answers, so the hub connects) but its firmware takes
    /// display control only via DDC/CI - the serial FF CC frame is a silent
    /// no-op there, so a connected hub alone must not select serial.
    /// </summary>
    private bool SerialTransportSelected =>
        _hub.IsConnected && _hub.Variant != Y70DisplayProtocol.VariantTruly;

    private void ApplySerial(bool screenOn, int pct)
    {
        // Firmware acts on the percentage (backlight) byte: screen-off must
        // send 0; a nonzero percentage keeps the panel lit even with the off
        // flag set. Screen-on floors to the firmware's minimum. Matches legacy
        // Y70TouchInfiniteController: off => SetCurrentBrightness(false, 0).
        var effective = screenOn ? Math.Max(pct, Y70DisplayProtocol.MinBrightnessOnPercent) : 0;
        var ok = _hub.SetBrightnessPower(screenOn, effective);
        LogApply($"transport=serial ok={ok}");
    }

    // Reference Y70DDCCIHelper gates DDC writes to >=100ms intervals
    // (SETTING_DEBOUNCE_TIME); the brightness slider posts per drag tick and a
    // DDC/CI write is slow, so in-window writes collapse to one trailing write
    // of the latest value. Power toggles are rare and write immediately.
    private const int DdcWriteIntervalMs = 100;
    // Guards the pending/last/timer fields; never held across the pipe RPC.
    private readonly object _ddcStateLock = new();
    // Serializes the actual RPC writes so overlapped route threads cannot
    // land out of order; each writer sends the latest pending value, so a
    // stale thread at worst duplicates the newest write.
    private readonly object _ddcRpcLock = new();
    // TickCount64 is >= 0, so -interval keeps the first write immediate
    // (long.MinValue would wrap the subtraction below negative).
    private long _lastDdcBrightnessWriteAt = -DdcWriteIntervalMs;
    private int _pendingDdcBrightness;
    private Timer? _ddcBrightnessFlush;
    private bool _disposed;

    private void DdcSetBrightnessCoalesced(int pct)
    {
        lock (_ddcStateLock)
        {
            if (_disposed) return;
            _pendingDdcBrightness = pct;
            var now = Environment.TickCount64;
            if (now - _lastDdcBrightnessWriteAt < DdcWriteIntervalMs)
            {
                _ddcBrightnessFlush ??= new Timer(_ => WritePendingDdcBrightness(), null, Timeout.Infinite, Timeout.Infinite);
                _ddcBrightnessFlush.Change(DdcWriteIntervalMs, Timeout.Infinite);
                return;
            }
            // Pending is written below; a still-armed trailing flush would
            // only duplicate it inside the write interval.
            _ddcBrightnessFlush?.Change(Timeout.Infinite, Timeout.Infinite);
            _lastDdcBrightnessWriteAt = now;
        }
        WritePendingDdcBrightness();
    }

    /// <summary>
    /// Immediate path and timer flush both land here. Never throws: the
    /// helper pipe RPC surfaces IOException / ObjectDisposedException across
    /// a helper restart, and an unhandled exception in a Timer callback is
    /// process-fatal.
    /// </summary>
    private void WritePendingDdcBrightness()
    {
        try
        {
            lock (_ddcRpcLock)
            {
                int pct;
                lock (_ddcStateLock)
                {
                    pct = _pendingDdcBrightness;
                    _lastDdcBrightnessWriteAt = Environment.TickCount64;
                }
                DdcWriteBrightness(pct);
            }
        }
        catch (Exception ex)
        {
            LogApply($"transport=ddc error {ex.GetType().Name}: {ex.Message}");
        }
    }

    // DDC/CI path (Truly / GW). UNVERIFIED - see class remarks. Brightness is
    // written raw (no MinBrightnessOnPercent floor) and independently of the
    // power state, matching the reference Y70TouchDdcciDeviceBase.
    private void DdcWriteBrightness(int pct)
    {
        var id = DdcDisplayId();
        if (id is null)
        {
            LogApply("transport=none (no reachable display; store-only)");
            return;
        }
        var ok = _ddc.SetVcp(id, Y70DisplayProtocol.VcpBrightness, pct);
        LogApply($"transport=ddc id={id} vcp=brightness ok={ok}");
    }

    private void DdcSetPower(bool screenOn)
    {
        try
        {
            lock (_ddcRpcLock)
            {
                var id = DdcDisplayId();
                if (id is null)
                {
                    LogApply("transport=none (no reachable display; store-only)");
                    return;
                }
                var ok = _ddc.SetVcp(id, Y70DisplayProtocol.VcpPower, screenOn ? Y70DisplayProtocol.VcpPowerOn : Y70DisplayProtocol.VcpPowerStandby);
                LogApply($"transport=ddc id={id} vcp=power on={screenOn} ok={ok}");
            }
        }
        catch (Exception ex)
        {
            LogApply($"transport=ddc error {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The brightness slider posts on every drag tick; log transitions only so
    // a drag emits one line while transport/result changes stay visible.
    private string? _lastApplyLog;

    private void LogApply(string detail)
    {
        if (detail == _lastApplyLog) return;
        _lastApplyLog = detail;
        ServiceLog.Info($"[y70-display] apply {detail}");
    }

    private string? DdcDisplayId() => _ddc.FindDisplayIdByHardwareName(Y70DisplayProtocol.DdcPanelHardwareNames);

    public void Dispose()
    {
        lock (_ddcStateLock)
        {
            _disposed = true;
            _ddcBrightnessFlush?.Dispose();
            _ddcBrightnessFlush = null;
        }
    }
}
