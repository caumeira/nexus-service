#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the HYTE Y70 panel via <c>ChangeDisplaySettingsEx</c>. Runs inside
/// the user-session helper so the change takes effect on the user's desktop.
/// Identifies the Y70 by the same set of panel-controller hardware names
/// nexus-overlay uses (<see cref="Y70DisplayProtocol.DdcPanelHardwareNames"/>),
/// matched as a substring of the monitor's PnP DeviceID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayOrientationProvider : IDisplayOrientationProvider
{

    public (bool Ok, string Error) SetY70Orientation(string orientation)
    {
        var device = FindY70AdapterDevice();
        if (string.IsNullOrEmpty(device))
        {
            // Not an error: the Y70 simply is not attached. The persisted
            // orientation will be re-applied when it next connects.
            return (true, "y70 panel not attached");
        }
        // No cover: the Y70 kiosk reconciles its bounds through a slower,
        // separate path (two service HTTP round-trips, not the in-process
        // refit promoted-monitor kiosks use - see nexus-overlay
        // PanelKioskWindow's WM_DISPLAYCHANGE handling), so RotationCoverHoldMs
        // would not span its repaint and a black cover would just add its own
        // visible flash.
        return ApplyToAdapter(device, orientation, coverColorHex: null);
    }

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
    {
        if (string.IsNullOrEmpty(displayId)) return (false, "missing display id");
        foreach (var adapterName in EnumerateMonitorAdapters())
        {
            var (id, _, _, _, _) = WindowsDisplayIdentity.ResolveIdentity(adapterName);
            if (string.Equals(id, displayId, StringComparison.Ordinal))
            {
                return ApplyToAdapter(adapterName, orientation, coverColorHex);
            }
        }
        // Unlike the Y70 path, the caller targeted a specific monitor.
        return (false, "display not found");
    }

    /// <summary>
    /// Shared ChangeDisplaySettingsEx core for both rotation paths.
    /// <paramref name="coverColorHex"/> null disables the pre-rotation cover
    /// entirely (the Y70 path); a non-null string (possibly empty) attempts
    /// it, with empty meaning "no themed colour, use opaque black".
    /// </summary>
    private static (bool Ok, string Error) ApplyToAdapter(string device, string orientation, string? coverColorHex)
    {
        if (!TryParseOrientation(orientation, out var dmdo))
        {
            return (false, $"unknown orientation '{orientation}'");
        }

        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return (false, "EnumDisplaySettings failed");
        }
        var fromOrientation = devMode.dmDisplayOrientation;
        if (fromOrientation == dmdo) return (true, $"already {orientation}");

        // Rotating between landscape <-> portrait flips the active resolution
        // axes. Failing to swap dmPelsWidth/dmPelsHeight makes Windows reject
        // the call with DISP_CHANGE_BADMODE.
        var fromPortrait = fromOrientation == DMDO_90 || fromOrientation == DMDO_270;
        var toPortrait = dmdo == DMDO_90 || dmdo == DMDO_270;
        var swapsAxes = fromPortrait != toPortrait;
        var oldWidth = devMode.dmPelsWidth;
        var oldHeight = devMode.dmPelsHeight;
        if (swapsAxes)
        {
            (devMode.dmPelsWidth, devMode.dmPelsHeight) = (devMode.dmPelsHeight, devMode.dmPelsWidth);
        }
        devMode.dmDisplayOrientation = dmdo;
        devMode.dmFields |= DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;

        // Only an axis swap exposes new desktop area at the monitor's origin;
        // a 180-degree flip keeps the same rect, so there is nothing to cover.
        var cover = IntPtr.Zero;
        if (swapsAxes && coverColorHex is not null)
        {
            cover = TryCreateRotationCover(
                device, devMode.dmPositionX, devMode.dmPositionY,
                oldWidth, oldHeight, devMode.dmPelsWidth, devMode.dmPelsHeight,
                coverColorHex);
        }
        try
        {
            var rc = ChangeDisplaySettingsExW(device, ref devMode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (cover != IntPtr.Zero && rc == DISP_CHANGE_SUCCESSFUL)
            {
                // Measured against nothing on this box (no hardware here) -
                // reasoned from nexus-overlay's refit path (77286d1): a bounds
                // change on an already-live kiosk drives SetWindowPos ->
                // WM_SIZE -> Ctrl_put_Bounds on the SAME already-painted
                // WebView2 controller, no destroy/renavigate/first-paint. A
                // resize-only reflow of already-rendered content is a small
                // number of compositor frames (60Hz = ~16.7ms/frame); this is
                // a short margin over that, not a hardware bench number -
                // shorten it if a live rotation shows the cover outlasting
                // the repaint.
                Thread.Sleep(RotationCoverHoldMs);
            }
            return rc switch
            {
                DISP_CHANGE_SUCCESSFUL => (true, $"applied {orientation} from={fromOrientation} set={devMode.dmPelsWidth}x{devMode.dmPelsHeight}"),
                DISP_CHANGE_RESTART    => (true, "restart required"),
                DISP_CHANGE_BADMODE    => (false, "mode not supported"),
                DISP_CHANGE_BADFLAGS   => (false, "bad flags"),
                DISP_CHANGE_BADPARAM   => (false, "bad parameter"),
                DISP_CHANGE_FAILED     => (false, "ChangeDisplaySettings failed"),
                DISP_CHANGE_NOTUPDATED => (false, "registry not updated"),
                _                       => (false, $"unknown DISP_CHANGE {rc}"),
            };
        }
        finally
        {
            // Every exit path (including an exception above) must reach this,
            // or a stuck cover blanks the panel until the helper restarts.
            if (cover != IntPtr.Zero)
            {
                DestroyRotationCover(cover);
            }
        }
    }

    private const int RotationCoverHoldMs = 120;

    private static bool TryParseOrientation(string value, out uint dmdo)
    {
        switch (value)
        {
            case "Landscape":         dmdo = DMDO_DEFAULT; return true;
            case "Portrait":          dmdo = DMDO_90;      return true;
            case "LandscapeFlipped":  dmdo = DMDO_180;     return true;
            case "PortraitFlipped":   dmdo = DMDO_270;     return true;
            default:                  dmdo = DMDO_DEFAULT; return false;
        }
    }

    /// <summary>
    /// Walks <c>EnumDisplayMonitors</c> + <c>EnumDisplayDevices</c> to find the
    /// adapter <c>\\.\DISPLAYn</c> hosting a HYTE panel. Mirrors
    /// <c>nexus-overlay/src/PanelDisplay.cs</c>. Calling
    /// <c>EnumDisplayDevicesW(null, ...)</c> to enumerate adapters is unreliable
    /// across CLR null-string marshaling paths; the EDM callback gives us a
    /// non-null <c>szDevice</c> for every active monitor.
    /// </summary>
    private static string FindY70AdapterDevice()
    {
        var monitors = EnumerateMonitorAdapters();
        foreach (var adapterName in monitors)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterName, 0, ref dd, 0)) continue;
            var deviceId = dd.DeviceID ?? "";
            foreach (var name in Y70DisplayProtocol.DdcPanelHardwareNames)
            {
                if (deviceId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return adapterName;
                }
            }
        }
        return "";
    }

    private static List<string> EnumerateMonitorAdapters()
    {
        var list = new List<string>();
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info) && !string.IsNullOrEmpty(info.szDevice))
            {
                list.Add(info.szDevice);
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Shows a solid-colour, topmost, non-activating window over the union of
    /// <paramref name="device"/>'s pre- and post-rotation rect, so the strip
    /// ChangeDisplaySettingsEx exposes never shows raw desktop wallpaper.
    /// Skipped (returns <see cref="IntPtr.Zero"/>) whenever that union would
    /// spill onto a neighbouring monitor: a flash on this panel is the
    /// existing bug, a flash on the user's OTHER display would be worse.
    /// </summary>
    private static IntPtr TryCreateRotationCover(
        string device, int x, int y, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, string coverColorHex)
    {
        try
        {
            var width = (int)Math.Max(oldWidth, newWidth);
            var height = (int)Math.Max(oldHeight, newHeight);
            if (UnionHitsAnotherMonitor(device, x, y, x + width, y + height))
            {
                return IntPtr.Zero;
            }
            if (!EnsureCoverWindowClassRegistered())
            {
                return IntPtr.Zero;
            }

            var hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                CoverWindowClassName, "", WS_POPUP,
                x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            PaintCover(hwnd, width, height, coverColorHex);
            return hwnd;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>True when the given rect overlaps any monitor OTHER than <paramref name="device"/>.</summary>
    private static bool UnionHitsAnotherMonitor(string device, int left, int top, int right, int bottom)
    {
        var hit = false;
        bool Cb(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;
            if (string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase)) return true;
            var m = info.rcMonitor;
            if (left < m.Right && right > m.Left && top < m.Bottom && bottom > m.Top)
            {
                hit = true;
                return false; // ok to stop early: EnumDisplayMonitors ends on a false return
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Cb, IntPtr.Zero);
        return hit;
    }

    private static void PaintCover(IntPtr hwnd, int width, int height, string coverColorHex)
    {
        var brush = CreateSolidBrush(ToColorRef(coverColorHex));
        var hdc = GetDC(hwnd);
        if (hdc != IntPtr.Zero)
        {
            var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            FillRect(hdc, ref rect, brush);
            ReleaseDC(hwnd, hdc);
        }
        DeleteObject(brush);
    }

    private static void DestroyRotationCover(IntPtr hwnd)
    {
        try { DestroyWindow(hwnd); } catch { }
    }

    /// <summary>"#rrggbb" to a COLORREF (0x00bbggrr); unparsable or empty falls back to opaque black.</summary>
    private static uint ToColorRef(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (uint)((b << 16) | (g << 8) | r);
        }
        return 0; // black
    }

    private static bool _coverClassRegistered;
    private static readonly object _coverClassLock = new();
    private static WndProcDelegate? _pinnedCoverProc;

    /// <summary>
    /// Registers the cover window class once per process; every call after
    /// the first is a no-op. The class background is a stock black brush -
    /// PaintCover immediately overpaints with the resolved colour, but any
    /// erase that lands before that call (a fresh HWND's first composite)
    /// still shows solid black rather than whatever was behind it, matching
    /// nexus-overlay's PanelKioskWindow class background for the same
    /// newly-exposed-region race.
    /// </summary>
    private static bool EnsureCoverWindowClassRegistered()
    {
        lock (_coverClassLock)
        {
            if (_coverClassRegistered) return true;
            _pinnedCoverProc = CoverWndProc;
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _pinnedCoverProc,
                lpszClassName = CoverWindowClassName,
                hInstance = GetModuleHandleW(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
            };
            if (RegisterClassExW(ref cls) == 0) return false;
            _coverClassRegistered = true;
            return true;
        }
    }

    private static IntPtr CoverWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hwnd, msg, wParam, lParam);

    // -- Win32 ---------------------------------------------------------------

    private const int CCHDEVICENAME = 32;
    private const int CCHDEVICESTRING = 128;
    private const int CCHDEVICEID = 128;
    private const int CCHDEVICEKEY = 128;

    private const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);

    private const uint DMDO_DEFAULT = 0;
    private const uint DMDO_90 = 1;
    private const uint DMDO_180 = 2;
    private const uint DMDO_270 = 3;

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYORIENTATION = 0x00800000;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;

    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const int DISP_CHANGE_BADFLAGS = -4;
    private const int DISP_CHANGE_BADPARAM = -5;

    private const string CoverWindowClassName = "NexusRotationCoverWnd";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int BLACK_BRUSH = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICESTRING)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEID)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICEKEY)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(
        string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);
}
#endif
