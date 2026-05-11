using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qos.Service.Platform.Windows;

/// <summary>
/// System tray icon for qos-service on Windows.
/// Pure Win32 - no WinForms. Creates a NotifyIcon in the system tray
/// with right-click menu (Open qOS / Open in Browser / Device Panel / Exit).
/// </summary>
public static class TrayIcon
{
    private const int NIM_ADD = 0x00;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 88;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int IDM_OPEN_BROWSER = 1;
    private const int IDM_EXIT = 2;
    private const int IDM_OPEN_APP = 3;
    private const int IDM_TOGGLE_PANEL = 4;
    private const int IDM_TOGGLE_DESKTOP_TOPMOST = 5;
    private const int MF_SEPARATOR = 0x0800;
    private const int MF_CHECKED = 0x0008;
    private const int MF_UNCHECKED = 0x0000;
    private const int DefaultServicePort = 9400;

    private static int _port;
    private static Action? _onExit;
    private static Action? _onTogglePanel;
    private static Func<bool>? _isPanelRunning;
    private static Action? _onToggleOverlayTopmost;
    private static Func<bool>? _isOverlayTopmost;
    private static Func<bool>? _hasOverlayWidgets;
    private static WndProcDelegate? _pinnedProc; // prevent GC
    private static readonly object _sync = new();
    private static Thread? _thread;
    private static bool _requestedVisible;
    private static bool _visible;
    private static bool _iconDataReady;
    private static NOTIFYICONDATA _nid;

    // Tracks the msedge.exe instance launched by OpenLocalWindow so we can
    // focus the existing window on the next click instead of spawning a
    // second one. Because we pass a dedicated --user-data-dir, the process
    // we start IS the top-level Edge browser for that profile - so its
    // MainWindowHandle resolves to the --app window.
    private static System.Diagnostics.Process? _appProcess;
    // Pinned callback - EnumWindows requires a delegate that isn't GC'd
    // mid-enumeration.
    private static EnumWindowsDelegate? _pinnedEnumProc;

    public static void Configure(int servicePort, Action onExit, Action? onTogglePanel = null, Func<bool>? isPanelRunning = null)
    {
        _port = servicePort;
        _onExit = onExit;
        _onTogglePanel = onTogglePanel;
        _isPanelRunning = isPanelRunning;
    }

    public static void ConfigureDesktop(
        Action onToggleOverlayTopmost,
        Func<bool> isOverlayTopmost,
        Func<bool> hasOverlayWidgets)
    {
        _onToggleOverlayTopmost = onToggleOverlayTopmost;
        _isOverlayTopmost = isOverlayTopmost;
        _hasOverlayWidgets = hasOverlayWidgets;
    }

    public static void Show(int servicePort, Action onExit, Action? onTogglePanel = null, Func<bool>? isPanelRunning = null)
    {
        Configure(servicePort, onExit, onTogglePanel, isPanelRunning);
        SetVisible(true);
    }

    public static void Hide() => SetVisible(false);

    public static void SetVisible(bool visible)
    {
        lock (_sync)
        {
            _requestedVisible = visible;
            if (visible && _thread is null)
            {
                _thread = new Thread(Run);
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.IsBackground = true;
                _thread.Start();
            }

            ApplyIconVisibilityNoThrow();
        }
    }

    private static void ApplyIconVisibilityNoThrow()
    {
        if (!_iconDataReady)
        {
            return;
        }

        try
        {
            if (_requestedVisible && !_visible)
            {
                var nid = _nid;
                Shell_NotifyIcon(NIM_ADD, ref nid);
                _visible = true;
            }
            else if (!_requestedVisible && _visible)
            {
                var nid = _nid;
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _visible = false;
            }
        }
        catch { /* tray is non-critical */ }
    }

    private static void Run()
    {
        try
        {
            _pinnedProc = Proc;

            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _pinnedProc,
                lpszClassName = "qOSTrayWnd",
                hInstance = GetModuleHandle(null),
            };

            if (RegisterClassEx(ref cls) == 0)
            {
                ResetThreadState();
                return;
            }

            var hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                ResetThreadState();
                return;
            }

            var nid = new NOTIFYICONDATA();
            nid.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = hwnd;
            nid.uID = 1;
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_TRAYICON;
            // Load icon: try embedded exe resource first, then icon.ico file, then default
            IntPtr hIcon = IntPtr.Zero;
            // 1) Embedded resource (set via <ApplicationIcon> in csproj) - resource ID 32512
            hIcon = LoadImage(cls.hInstance, new IntPtr(32512), 1 /*IMAGE_ICON*/,
                GetSystemMetrics(11 /*SM_CXSMICON*/), GetSystemMetrics(12 /*SM_CYSMICON*/), 0);
            // 2) icon.ico file next to exe
            if (hIcon == IntPtr.Zero)
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    hIcon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 0, 0,
                        0x00000010 /*LR_LOADFROMFILE*/ | 0x00000040 /*LR_DEFAULTSIZE*/);
                }
            }
            // 3) Default Windows icon
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
            }

            nid.hIcon = hIcon;
            nid.szTip = "qOS";

            lock (_sync)
            {
                _nid = nid;
                _iconDataReady = true;
                ApplyIconVisibilityNoThrow();
            }

            // Message pump
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            lock (_sync)
            {
                if (_visible)
                {
                    var deleteNid = _nid;
                    Shell_NotifyIcon(NIM_DELETE, ref deleteNid);
                }
                _visible = false;
                _iconDataReady = false;
                _thread = null;
            }
        }
        catch
        {
            // Silently fail - tray is non-critical
            lock (_sync)
            {
                _visible = false;
                _iconDataReady = false;
                _thread = null;
            }
        }
    }

    private static void ResetThreadState()
    {
        lock (_sync)
        {
            _visible = false;
            _iconDataReady = false;
            _thread = null;
        }
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                var ev = lParam.ToInt32() & 0xFFFF;
                if (ev == WM_RBUTTONUP)
                {
                    POINT pt;
                    GetCursorPos(out pt);
                    var menu = CreatePopupMenu();
                    AppendMenu(menu, 0, IDM_OPEN_APP, "Open qOS");
                    AppendMenu(menu, 0, IDM_OPEN_BROWSER, "Open in Browser");
                    AppendMenu(menu, MF_SEPARATOR, 0, "");
                    var panelFlag = (_isPanelRunning?.Invoke() ?? false) ? MF_CHECKED : MF_UNCHECKED;
                    AppendMenu(menu, panelFlag, IDM_TOGGLE_PANEL, "Device Panel");
                    if (_hasOverlayWidgets?.Invoke() ?? false)
                    {
                        var topmostFlag = (_isOverlayTopmost?.Invoke() ?? false) ? MF_CHECKED : MF_UNCHECKED;
                        AppendMenu(menu, topmostFlag, IDM_TOGGLE_DESKTOP_TOPMOST, "Widgets always on top");
                    }
                    AppendMenu(menu, MF_SEPARATOR, 0, "");
                    AppendMenu(menu, 0, IDM_EXIT, "Exit");
                    SetForegroundWindow(hwnd);
                    TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
                    DestroyMenu(menu);
                }
                else if (ev == WM_LBUTTONDBLCLK)
                {
                    OpenLocalWindow();
                }
            }
            else if (msg == WM_COMMAND)
            {
                var id = wParam.ToInt32() & 0xFFFF;
                if (id == IDM_OPEN_APP)
                {
                    OpenLocalWindow();
                }
                else if (id == IDM_OPEN_BROWSER)
                {
                    OpenDashboard();
                }
                else if (id == IDM_TOGGLE_PANEL)
                {
                    _onTogglePanel?.Invoke();
                }
                else if (id == IDM_TOGGLE_DESKTOP_TOPMOST)
                {
                    _onToggleOverlayTopmost?.Invoke();
                }
                else if (id == IDM_EXIT)
                {
                    _onExit?.Invoke();
                }
            }
        }
        catch { /* don't let WndProc crash kill the service */ }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OpenDashboard(int servicePort = 0)
    {
        var port = ResolveDashboardPort(servicePort);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = $"http://localhost:{port}/",
            UseShellExecute = true,
        });
    }

    public static void OpenLocalWindow(int servicePort = 0)
    {
        var port = ResolveDashboardPort(servicePort);

        // Fast path: we still hold a handle to the process we launched and
        // it's alive. Just focus its window.
        if (_appProcess is not null && !_appProcess.HasExited)
        {
            try
            {
                _appProcess.Refresh();
                var h = _appProcess.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    FocusWindow(h);
                    return;
                }
            }
            catch { /* fall through to fallback / spawn */ }
        }

        // Fallback: we lost the process reference (service restarted with
        // the window still open, Edge was updated, etc.). Sweep top-level
        // windows and focus the one whose title is "qOS" before
        // spawning a duplicate.
        var existing = FindExistingAppWindow();
        if (existing != IntPtr.Zero)
        {
            FocusWindow(existing);
            return;
        }

        var url = $"http://localhost:{port}/";
        var edgePath = FindEdge();
        if (edgePath is not null)
        {
            var dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qos-app");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add($"--app={url}");
            psi.ArgumentList.Add($"--user-data-dir={dataDir}");
            _appProcess = System.Diagnostics.Process.Start(psi);
        }
        else
        {
            OpenDashboard(port);
        }
    }

    private static int ResolveDashboardPort(int servicePort)
    {
        if (servicePort > 0)
        {
            return servicePort;
        }

        return _port > 0 ? _port : DefaultServicePort;
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        try
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            SetForegroundWindow(hwnd);
        }
        catch { /* best-effort */ }
    }

    private static IntPtr FindExistingAppWindow()
    {
        IntPtr result = IntPtr.Zero;
        _pinnedEnumProc = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }
            // Skip tool windows. Our floating desktop overlays
            // (qos-overlay.exe) carry WS_EX_TOOLWINDOW so they don't
            // show in Alt-Tab; they also have a title that starts with
            // "qOS", which would otherwise match here. The dashboard
            // window is a regular Edge --app top-level (no toolwindow bit).
            const int GWL_EXSTYLE_LOCAL = -20;
            const int WS_EX_TOOLWINDOW_LOCAL = 0x00000080;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE_LOCAL);
            if ((ex & WS_EX_TOOLWINDOW_LOCAL) != 0)
            {
                return true;
            }
            int len = GetWindowTextLength(hwnd);
            if (len == 0)
            {
                return true;
            }
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            // Edge --app puts the page title verbatim in the window
            // caption. Our SPA is titled "qOS"; match any window that
            // starts with that so we still catch "qOS - <section>"
            // style titles if we ever add them.
            if (title.StartsWith("qOS", StringComparison.OrdinalIgnoreCase))
            {
                result = hwnd;
                return false; // stop enumeration
            }
            return true;
        };
        EnumWindows(_pinnedEnumProc, IntPtr.Zero);
        return result;
    }

    private static string? FindEdge()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        foreach (var path in candidates)
        {
            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    // Delegates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Structs
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public int time;
        public int ptX, ptY;
    }

    // Imports
    [DllImport("kernel32")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, int fuLoad);
    [DllImport("user32", EntryPoint = "LoadImageW")] private static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, int type, int cx, int cy, int fuLoad);
    [DllImport("user32")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
    [DllImport("user32")] private static extern bool TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);

    // Single-instance window focus path
    private const int SW_RESTORE = 9;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsDelegate enumProc, IntPtr lParam);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
    [DllImport("user32", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int nIndex);
}
