using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Qos.Service.Panel;

/// <summary>
/// Launches Edge in kiosk mode on a secondary monitor to host the panel widget
/// engine. Opens http://localhost:{port}/panel fullscreen with no chrome -
/// borderless, no address bar, no tabs.
///
/// Detection priority:
/// 1. Any secondary monitor (the Y70 touch panel shows up as a second display)
/// 2. Fallback: no launch if only a primary display is connected
///
/// Uses msedge.exe --kiosk which is preinstalled on Windows 10/11.
/// Zero additional dependencies.
/// </summary>
public sealed class PanelKioskLauncher
{
    private Process? _edgeProcess;
    private readonly int _servicePort;
    private readonly Auth.TokenService? _tokens;

    private static readonly string PidFilePath = Path.Combine(
        GetConfigDir(), "panel-edge-pid.txt");

    public PanelKioskLauncher(int servicePort = 9400, Auth.TokenService? tokens = null)
    {
        _servicePort = servicePort;
        _tokens = tokens;
    }

    public bool IsRunning => _edgeProcess is { HasExited: false };

    /// <summary>
    /// Find a secondary monitor and launch the panel view on it.
    /// Returns true if launched, false if no suitable display found.
    /// </summary>
    public bool Launch()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("[panel] not on Windows, skipping display launch");
            return false;
        }

        if (IsRunning)
        {
            Console.WriteLine("[panel] already running");
            return true;
        }

        var display = FindDisplay();
        if (display == null)
        {
            Console.WriteLine("[panel] no secondary monitor found");
            return false;
        }

        // Pass auth token in URL so the kiosk Edge session doesn't need localStorage
        var token = _tokens?.Token ?? "";
        var url = $"http://localhost:{_servicePort}/panel?token={Uri.EscapeDataString(token)}";

        try
        {
            var userDataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "qOS", "PanelKiosk");

            var psi = new ProcessStartInfo
            {
                FileName = "msedge.exe",
                UseShellExecute = true,
            };
            // Kiosk mode: fullscreen, no chrome, no taskbar icon, no escape banner.
            psi.ArgumentList.Add("--kiosk");
            psi.ArgumentList.Add(url);
            psi.ArgumentList.Add($"--window-position={display.Value.X},{display.Value.Y}");
            psi.ArgumentList.Add($"--window-size={display.Value.Width},{display.Value.Height}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--disable-session-crashed-bubble");
            psi.ArgumentList.Add("--disable-infobars");
            psi.ArgumentList.Add("--disable-features=msEdgeSidebarV2,msEdgeSplitWindow,msEdgeWorkspaces,msEdgeJSONViewer");
            psi.ArgumentList.Add("--disable-sync");
            psi.ArgumentList.Add("--disable-extensions");
            psi.ArgumentList.Add("--disable-notifications");
            psi.ArgumentList.Add("--disable-background-mode");
            psi.ArgumentList.Add("--overscroll-history-navigation=0");
            psi.ArgumentList.Add("--disable-pinch");
            psi.ArgumentList.Add($"--user-data-dir={userDataDir}");
            // Force separate process - prevents merging into existing Edge instance
            psi.ArgumentList.Add("--no-proxy-server");

            _edgeProcess = Process.Start(psi);
            if (_edgeProcess is not null)
            {
                WritePidFile(_edgeProcess.Id);
            }

            Console.WriteLine($"[panel] launched Edge kiosk on {display.Value.Width}x{display.Value.Height} at ({display.Value.X},{display.Value.Y})");

            // Hide kiosk Edge from taskbar (kiosk shows in taskbar when another Edge is open)
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(1000);
                    if (_edgeProcess is null or { HasExited: true })
                    {
                        break;
                    }

                    HideEdgeFromTaskbar(_edgeProcess.Id);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel] failed to launch Edge: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        try
        {
            if (_edgeProcess is { HasExited: false })
            {
                _edgeProcess.Kill(entireProcessTree: true);
                _edgeProcess = null;
                Console.WriteLine("[panel] closed");
            }
            DeletePidFile();
        }
        catch { }
    }

    /// <summary>
    /// Kill any orphaned Edge kiosk from a previous crash.
    /// Called on service startup before launching a new instance.
    /// </summary>
    public static void CleanupOrphans()
    {
        try
        {
            if (!File.Exists(PidFilePath))
            {
                return;
            }

            var text = File.ReadAllText(PidFilePath).Trim();
            if (!int.TryParse(text, out var pid))
            { DeletePidFile(); return; }

            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.ProcessName.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                    Console.WriteLine($"[panel] killed orphan Edge kiosk (pid {pid})");
                }
                proc.Dispose();
            }
            catch { /* process already gone or access denied */ }

            DeletePidFile();
        }
        catch { }
    }

    private static void WritePidFile(int pid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            File.WriteAllText(PidFilePath, pid.ToString());
        }
        catch { }
    }

    private static void DeletePidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
            {
                File.Delete(PidFilePath);
            }
        }
        catch { }
    }

    private static string GetConfigDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qOS");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine(home, "Library", "Application Support", "qOS")
            : Path.Combine(home, ".config", "qOS");
    }

    private static readonly string[] PanelDisplayNames =
    {
        "HYTE Y70Touch",
        "HYTE Y70ti",
        "Y70TI",
        "HYTE Y70ti gw",
        "HYTE GT LCD",
    };

    /// <summary>
    /// Check whether any connected display matches a known panel device (Y70/Y80).
    /// Returns true if found, without launching - used by auto-launch logic to
    /// decide whether to start the kiosk on boot.
    /// </summary>
    public static bool IsPanelDisplayConnected()
    {
        var (_, name) = FindPanelDisplay();
        return name is not null;
    }

    private static (DisplayRect? rect, string? matchedName) FindPanelDisplay()
    {
        var monitors = new System.Collections.Generic.List<(DisplayRect rect, string deviceName, bool isPrimary)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, hdcMonitor, lprcMonitor, dwData) =>
        {
            var infoEx = new MONITORINFOEX();
            infoEx.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfoEx(hMonitor, ref infoEx))
            {
                var rect = new DisplayRect(
                    infoEx.rcMonitor.left,
                    infoEx.rcMonitor.top,
                    infoEx.rcMonitor.right - infoEx.rcMonitor.left,
                    infoEx.rcMonitor.bottom - infoEx.rcMonitor.top
                );
                bool isPrimary = (infoEx.dwFlags & 1) != 0;
                string deviceName = infoEx.szDevice ?? "";
                monitors.Add((rect, deviceName, isPrimary));
            }
            return true;
        }, IntPtr.Zero);

        // Try to match a known panel display by EDID name via EnumDisplayDevices.
        foreach (var (rect, deviceName, _) in monitors)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevicesW(deviceName, 0, ref dd, 1))
            {
                var monitorName = dd.DeviceString ?? "";
                foreach (var known in PanelDisplayNames)
                {
                    if (monitorName.Contains(known, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[panel] matched display: {monitorName} at ({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");
                        return (rect, monitorName);
                    }
                }
            }
        }

        // Fallback: first non-primary monitor (for testing without recognized hardware).
        if (monitors.Count < 2)
            return (null, null);
        foreach (var (rect, _, isPrimary) in monitors)
        {
            if (!isPrimary)
                return (rect, null);
        }
        return (monitors.Count > 1 ? monitors[1].rect : null, null);
    }

    private static DisplayRect? FindDisplay()
    {
        var (rect, name) = FindPanelDisplay();
        if (name is not null)
        {
            Console.WriteLine($"[panel] using recognized display: {name}");
        }
        else if (rect is not null)
        {
            Console.WriteLine("[panel] no recognized panel display - using first secondary monitor");
        }
        return rect;
    }

    private record struct DisplayRect(int X, int Y, int Width, int Height);

    // Win32 interop
    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    /// <summary>
    /// Hide Edge windows from the taskbar by setting WS_EX_TOOLWINDOW.
    /// Enumerates all windows belonging to the given process ID.
    /// Kiosk mode in Edge still shows in taskbar when another Edge is open -
    /// this removes it. Does NOT strip title bars (kiosk already handles that).
    /// </summary>
    private static void HideEdgeFromTaskbar(int pid)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_APPWINDOW = 0x40000;

        EnumWindows((hwnd, lParam) =>
        {
            GetWindowThreadProcessId(hwnd, out var wPid);
            if (wPid == pid && IsWindowVisible(hwnd))
            {
                var style = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((style & WS_EX_TOOLWINDOW) == 0)
                {
                    style = (style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                    SetWindowLong(hwnd, GWL_EXSTYLE, style);
                    // Flash hide/show to force taskbar refresh
                    ShowWindow(hwnd, 0);
                    ShowWindow(hwnd, 5);
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
}
