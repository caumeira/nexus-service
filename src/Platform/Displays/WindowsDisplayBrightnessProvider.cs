#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Windows display brightness via the in-box DDC/CI APIs in dxva2.dll
/// (GetMonitorBrightness / SetMonitorBrightness / Get/SetVCPFeature). Works for
/// external monitors that expose DDC over the video cable. Internal laptop
/// panels are not handled here; they need WmiMonitorBrightnessMethods or
/// IOCTL_VIDEO_*.
/// </summary>
public sealed class WindowsDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    private const int DefaultDdcWriteCooldownMs = 160;

    public string Hint => "";

    public IReadOnlyList<DisplayDto> Enumerate()
    {
        var results = new List<DisplayDto>();
        try
        {
            var monitors = EnumerateHMonitors();
            for (int idx = 0; idx < monitors.Count; idx++)
            {
                var entry = monitors[idx];
                var (id, name, manufacturer, model, isInternal) = ResolveIdentity(entry.AdapterDevice);
                // Show "Display 1", "Display 2", ... when no friendly name is
                // available (EnumDisplayDevices left DeviceString empty / fell
                // back to the raw \\.\DISPLAYn adapter token).
                if (string.IsNullOrEmpty(name) || name.StartsWith(@"\\.\", StringComparison.Ordinal))
                {
                    name = $"Display {idx + 1}";
                }
                var dto = new DisplayDto
                {
                    Id = id,
                    Name = name,
                    Manufacturer = manufacturer,
                    Model = model,
                    IsInternal = isInternal,
                };

                dto.BrightnessControl.UnsupportedReason = "Brightness control is not available for this display.";

                // Try to open physical monitors and check brightness support via
                // the same control path used for writes. DDC/CI VCP 0x10 is the
                // canonical luminance feature; GetMonitorBrightness is only a
                // compatibility fallback.
                if (TryGetPhysicalMonitor(entry.HMonitor, out var phys))
                {
                    try
                    {
                        if (TryGetVcp(phys, 0x10, out var cur, out var max))
                        {
                            dto.IsDdcCapable = true;
                            dto.Capabilities.Brightness = true;
                            dto.BrightnessControl = BuildSupportedBrightnessControl(
                                RawToPercent(cur, max),
                                DisplayBrightnessControlPaths.DdcCi);
                            dto.Capabilities.Contrast = TryGetVcp(phys, 0x12, out _, out _);
                        }
                        else if (TryGetMonitorBrightness(phys, out var min2, out var cur2, out var max2))
                        {
                            dto.IsDdcCapable = true;
                            dto.Capabilities.Brightness = true;
                            dto.BrightnessControl = BuildSupportedBrightnessControl(
                                RangeToPercent(cur2, min2, max2),
                                DisplayBrightnessControlPaths.DdcCi);
                            dto.Capabilities.Contrast = TryGetVcp(phys, 0x12, out _, out _);
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitor(phys);
                    }
                }
                results.Add(dto);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] enumerate failed: {ex.Message}");
        }
        return results;
    }

    public int? GetBrightness(string id)
    {
        if (!TryOpenById(id, out var phys)) return null;
        try
        {
            // Read VCP 0x10 (luminance) directly. GetVCPFeatureAndVCPFeatureReply
            // round-trips DDC to the monitor each time instead of returning the
            // OS-cached value that GetMonitorBrightness can serve, so the read
            // here actually reflects the panel's current backlight setting.
            if (TryGetVcp(phys, 0x10, out var cur, out var max)) return RawToPercent(cur, max);
            return TryGetMonitorBrightness(phys, out var min2, out var cur2, out var max2)
                ? RangeToPercent(cur2, min2, max2)
                : null;
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public DisplayBrightnessDto SetBrightness(string id, int percent)
    {
        var requested = ClampPercent(percent);
        if (!TryOpenById(id, out var phys))
        {
            return FailedBrightness(id, requested, "Display not found or brightness control unavailable.");
        }

        try
        {
            // Prefer the raw VCP path because it is the same cross-monitor
            // control exposed by DDC/CI. Some drivers make the DXVA brightness
            // wrapper look successful while not updating the physical monitor.
            if (TryGetVcp(phys, 0x10, out _, out var max) && max > 0)
            {
                var raw = PercentToRaw(requested, max);
                if (SetVCPFeature(phys, 0x10, raw))
                {
                    var applied = TryGetVcp(phys, 0x10, out var curAfter, out var maxAfter)
                        ? RawToPercent(curAfter, maxAfter)
                        : requested;
                    return AppliedBrightness(id, requested, applied);
                }
            }

            if (TryGetMonitorBrightness(phys, out var min2, out _, out var max2))
            {
                var raw = PercentToRange(requested, min2, max2);
                if (SetMonitorBrightness(phys, raw))
                {
                    var applied = TryGetMonitorBrightness(phys, out var minAfter, out var curAfter, out var maxAfter)
                        ? RangeToPercent(curAfter, minAfter, maxAfter)
                        : requested;
                    return AppliedBrightness(id, requested, applied);
                }
            }

            return FailedBrightness(id, requested, "Monitor rejected the brightness write.");
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id)
    {
        if (!TryOpenById(id, out var phys)) return new DisplayBrightnessWritePolicy();
        try
        {
            if (TryGetVcp(phys, 0x10, out _, out _) ||
                TryGetMonitorBrightness(phys, out _, out _, out _))
            {
                return new DisplayBrightnessWritePolicy
                {
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    MinWriteIntervalMs = DefaultDdcWriteCooldownMs,
                    ReadAfterWriteDelayMs = 0,
                    VerifyAfterWrite = false,
                };
            }
        }
        finally { DestroyPhysicalMonitor(phys); }

        return new DisplayBrightnessWritePolicy();
    }

    public DisplayVcpDto? GetVcp(string id, byte code)
    {
        if (!TryOpenById(id, out var phys)) return null;
        try
        {
            if (TryGetVcp(phys, code, out var cur, out var max))
            {
                return new DisplayVcpDto { Id = id, Code = code, Value = (int)cur, MaxValue = (int)max };
            }
            return null;
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public bool SetVcp(string id, byte code, int value)
    {
        if (value < 0) return false;
        if (!TryOpenById(id, out var phys)) return false;
        try
        {
            // Clamp to the monitor's reported MaxValue when available - some
            // monitors latch out-of-range writes into a state that requires a
            // power-cycle to recover.
            var write = (uint)value;
            if (TryGetVcp(phys, code, out _, out var max) && max > 0 && write > max)
            {
                write = max;
            }
            return SetVCPFeature(phys, code, write);
        }
        finally { DestroyPhysicalMonitor(phys); }
    }

    public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments)
    {
        if (nameFragments is null || nameFragments.Count == 0) return null;
        try
        {
            foreach (var entry in EnumerateHMonitors())
            {
                var rawDeviceId = ReadMonitorDeviceId(entry.AdapterDevice);
                if (string.IsNullOrEmpty(rawDeviceId)) continue;
                foreach (var fragment in nameFragments)
                {
                    if (rawDeviceId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Return the same stable id SetVcp/GetBrightness key off.
                        var (id, _, _, _, _) = ResolveIdentity(entry.AdapterDevice);
                        return id;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays-win] find by hardware name failed: {ex.Message}");
        }
        return null;
    }

    // Raw monitor PnP DeviceID (e.g. \\?\DISPLAY#RTK0004#...) for the first
    // child monitor of an adapter — used to match a controller name before we
    // collapse it to the sanitized stable id.
    private static string ReadMonitorDeviceId(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
        {
            monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0)) return "";
        }
        return monitor.DeviceID ?? "";
    }

    // Resolve an id back to a freshly-opened physical monitor handle. Caller
    // owns the handle via DestroyPhysicalMonitor. Lazy approach: re-enumerate
    // and match by id, since dxva2 handles aren't safe to cache.
    private bool TryOpenById(string id, out IntPtr phys)
    {
        phys = IntPtr.Zero;
        foreach (var entry in EnumerateHMonitors())
        {
            var (eid, _, _, _, _) = ResolveIdentity(entry.AdapterDevice);
            if (!string.Equals(eid, id, StringComparison.Ordinal)) continue;
            return TryGetPhysicalMonitor(entry.HMonitor, out phys);
        }
        return false;
    }

    private static bool TryGetPhysicalMonitor(IntPtr hMonitor, out IntPtr phys)
    {
        phys = IntPtr.Zero;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return false;
        var arr = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr)) return false;
        phys = arr[0].hPhysicalMonitor;
        // Free the description-bearing entries we don't keep.
        for (uint i = 1; i < count; i++) DestroyPhysicalMonitor(arr[i].hPhysicalMonitor);
        return phys != IntPtr.Zero;
    }

    private static bool TryGetMonitorBrightness(IntPtr phys, out uint min, out uint cur, out uint max)
    {
        min = 0; cur = 0; max = 0;
        try { return GetMonitorBrightness(phys, out min, out cur, out max); }
        catch { return false; }
    }

    private static bool TryGetVcp(IntPtr phys, byte code, out uint cur, out uint max)
    {
        cur = 0; max = 0;
        try
        {
            return GetVCPFeatureAndVCPFeatureReply(phys, code, IntPtr.Zero, out cur, out max);
        }
        catch { return false; }
    }

    private readonly record struct MonitorEntry(IntPtr HMonitor, string AdapterDevice);

    private static List<MonitorEntry> EnumerateHMonitors()
    {
        var list = new List<MonitorEntry>();
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info))
            {
                list.Add(new MonitorEntry(hMonitor, info.szDevice));
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return list;
    }

    // Returns (stableId, friendlyName, manufacturer3, model, isInternal).
    // Stable id is the EDID-derived portion of the monitor's PnP DeviceID
    // (survives reboots and cable shuffles); falls back to the adapter+index
    // identifier when EnumDisplayDevices doesn't expose the PnP id.
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    private static (string, string, string, string, bool) ResolveIdentity(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        bool ok = EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME);
        if (!ok)
        {
            // Retry without the interface-name flag for older drivers.
            monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            ok = EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0);
        }
        if (!ok)
        {
            return (adapterDeviceName, adapterDeviceName, "", "", false);
        }
        var deviceId = monitor.DeviceID ?? "";
        var friendly = string.IsNullOrWhiteSpace(monitor.DeviceString) ? adapterDeviceName : monitor.DeviceString;

        // DeviceID looks like \\?\DISPLAY#DEL41B7#5&abc&0&UID12345#{...guid...}
        // Take the part between the first and last '#' as the EDID-stable id.
        var stable = deviceId;
        var firstHash = deviceId.IndexOf('#');
        var lastHash = deviceId.LastIndexOf('#');
        var manufacturer = "";
        var model = "";
        if (firstHash > 0 && lastHash > firstHash)
        {
            stable = deviceId.Substring(firstHash + 1, lastHash - firstHash - 1);
            // "DEL41B7" -> "DEL" manufacturer EISA id, "41B7" hex product code
            var firstSegEnd = stable.IndexOf('#');
            if (firstSegEnd >= 7)
            {
                var seg = stable.Substring(0, firstSegEnd);
                manufacturer = seg.Substring(0, 3);
                model = seg.Length > 3 ? seg.Substring(3) : "";
            }
        }
        if (string.IsNullOrEmpty(stable))
        {
            // EnumDisplayDevices left DeviceID empty (some virtual / non-PnP
            // displays). Fall back to the adapter's trailing DISPLAYn segment
            // so the row still gets a stable handle for brightness round-trips.
            stable = AdapterIndexFallback(adapterDeviceName);
        }
        // Replace URL-hostile characters so the id round-trips through HTTP
        // path segments without escaping headaches.
        stable = SanitizeId(stable);

        // "Generic PnP Monitor" is the default DeviceString; prefer something
        // more user-friendly when we have manufacturer + model identifiers.
        if (!string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(model))
        {
            friendly = $"{manufacturer} {model}";
        }

        // Heuristic: classify a panel as internal by the absence of HDMI/DP
        // signal in the friendly name (laptop internal panels show as
        // "Built-in" / PnP IDs like LEN/AAP / output technology "internal").
        var isInternal = friendly.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0
                      || friendly.IndexOf("built-in", StringComparison.OrdinalIgnoreCase) >= 0;

        return (stable, friendly, manufacturer, model, isInternal);
    }

    // -- P/Invoke -----------------------------------------------------------

    private const int CCHDEVICENAME = 32;
    private const int CCHMONITORNAME = 32;
    private const int CCHDEVICESTRING = 128;
    private const int CCHDEVICEID = 128;
    private const int CCHDEVICEKEY = 128;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEID)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEKEY)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, IntPtr pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    private static string SanitizeId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            buf[i] = (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') ? c : '-';
        }
        return new string(buf);
    }

    private static string AdapterIndexFallback(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return "display-unknown";
        var lastSlash = adapterDeviceName.LastIndexOf('\\');
        var tail = lastSlash >= 0 ? adapterDeviceName[(lastSlash + 1)..] : adapterDeviceName;
        return string.IsNullOrEmpty(tail) ? "display-unknown" : tail.ToLowerInvariant();
    }

    private static DisplayBrightnessControlDto BuildSupportedBrightnessControl(int current, string controlPath) => new()
    {
        Supported = true,
        Min = 0,
        Max = 100,
        Current = ClampPercent(current),
        ControlPath = controlPath,
        WriteMode = DisplayBrightnessWriteModes.Coalesced,
        WriteCooldownMs = DefaultDdcWriteCooldownMs,
        VerifyAfterWrite = false,
    };

    private static DisplayBrightnessDto AppliedBrightness(string id, int requested, int applied)
    {
        applied = ClampPercent(applied);
        return new DisplayBrightnessDto
        {
            Id = id,
            RequestedBrightness = requested,
            AppliedBrightness = applied,
            Brightness = applied,
            Status = DisplayBrightnessWriteStatuses.Applied,
        };
    }

    private static DisplayBrightnessDto FailedBrightness(string id, int requested, string error) => new()
    {
        Id = id,
        RequestedBrightness = requested,
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Failed,
        Error = error,
    };

    private static int ClampPercent(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

    private static int RawToPercent(uint current, uint max)
    {
        if (max == 0) return ClampPercent((int)current);
        return ClampPercent((int)Math.Round(current * 100.0 / max));
    }

    private static uint PercentToRaw(int percent, uint max)
    {
        if (max == 0) return (uint)ClampPercent(percent);
        return (uint)Math.Round(ClampPercent(percent) * max / 100.0);
    }

    private static int RangeToPercent(uint current, uint min, uint max)
    {
        if (max <= min) return ClampPercent((int)current);
        var clamped = current < min ? min : current > max ? max : current;
        return ClampPercent((int)Math.Round((clamped - min) * 100.0 / (max - min)));
    }

    private static uint PercentToRange(int percent, uint min, uint max)
    {
        if (max <= min) return (uint)ClampPercent(percent);
        return min + (uint)Math.Round(ClampPercent(percent) * (max - min) / 100.0);
    }
}
#endif
