using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// System tray icon for nexus-service on Windows.
/// Pure Win32 - no WinForms. Creates a NotifyIcon in the system tray
/// with right-click menu (Open / Settings / Shut down).
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIcon
{
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;   // szInfo/szInfoTitle carry a balloon
    private const int NIIF_INFO = 0x01;  // info (i) glyph on the balloon
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 88;
    // Shell balloon notifications arrive through the same WM_TRAYICON
    // callback, with the notification code in the lParam low word (the
    // version-0 NOTIFYICONDATA encoding this tray already relies on for
    // mouse events). NIN_BALLOONUSERCLICK fires when the user clicks the
    // balloon (or its Action Center entry).
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int IDM_OPEN_APP = 1;
    private const int IDM_OPEN_SETTINGS = 2;
    private const int IDM_SHUTDOWN = 3;
    // Profile command ids occupy [IDM_PROFILE_BASE, IDM_PROFILE_BASE + count).
    // 0x100 stays well clear of the three fixed IDM_* ids above.
    private const int IDM_PROFILE_BASE = 0x100;
    private const int MF_STRING = 0x0000;
    private const int MF_SEPARATOR = 0x0800;
    private const int MF_CHECKED = 0x0008;
    private const int MF_UNCHECKED = 0x0000;
    private const int MF_POPUP = 0x0010;
    private const int DefaultServicePort = 9400;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_TIMER = 0x0113;
    // Wake posted to the window thread to arm the NIM_ADD retry when an add
    // fails off-thread (the service's cross-thread SetVisible push).
    private const int WM_ARM_ICON_RETRY = WM_USER + 90;
    // NIM_ADD retry poll (see StartIconRetry): timer id, interval, attempt cap.
    private const int IconRetryTimerId = 0xBEEF;
    private const uint IconRetryIntervalMs = 1000;
    private const int IconRetryMaxAttempts = 60;

    // uxtheme.dll ordinal 135: SetPreferredAppMode(int mode). The current
    // int-taking signature shipped in Windows 10 1903 (build 18362); the
    // earlier 1809 ABI used the same ordinal for AllowDarkModeForApp(bool),
    // so any call must be gated on the build number to avoid silently
    // coercing an int to BOOL on older systems.
    private const int APPMODE_DEFAULT = 0;
    private const int APPMODE_ALLOW_DARK = 1;
    private const int SetPreferredAppModeMinBuild = 18362;

    /// <summary>
    /// Raised on WM_DISPLAYCHANGE (monitor plug/unplug, resolution or
    /// arrangement change). The tray's hidden top-level window receives the
    /// system broadcast, making it the helper's display-change signal source.
    /// Fired on the message-pump thread — subscribers must not block.
    /// </summary>
    public static event Action? DisplayChanged;

    private static int _port;
    private static Action? _onExit;
    private static Action? _onTogglePanel;
    private static Func<bool>? _isPanelRunning;
    private static Action? _onToggleOverlayTopmost;
    private static Func<bool>? _isOverlayTopmost;
    private static Func<bool>? _hasOverlayWidgets;
    private static Action? _onToggleAutostart;
    private static Func<bool>? _isAutostartEnabled;
    private static Func<(System.Collections.Generic.IReadOnlyList<(string Id, string Name)> Items, string ActiveId)>? _getProfiles;
    private static Action<string>? _onSwitchProfile;
    // Rebuilt on every right-click menu open; maps submenu index to profile id.
    private static readonly System.Collections.Generic.List<string> _profileMenuIds = new();
    private static WndProcDelegate? _pinnedProc; // prevent GC
    private static readonly object _sync = new();
    private static Thread? _thread;
    private static bool _requestedVisible;
    private static bool _visible;
    private static bool _iconDataReady;
    private static NOTIFYICONDATA _nid;
    private static IntPtr _hwnd;
    // RegisterWindowMessage("TaskbarCreated") id. Explorer broadcasts this to
    // every top-level window when it (re)creates the taskbar - at first shell
    // start after a cold boot and on every Explorer restart. The prior
    // NIM_ADD registration dies with the old taskbar, so the icon must be
    // re-added on receipt or it never reappears.
    private static uint _taskbarCreatedMsg;
    private static bool _retryTimerArmed;
    private static int _iconRetryAttempts;

    // Race guard for rapid tray clicks: between spawning an Edge --app and
    // its window title becoming "Nexus*" (~1-2s), FindExistingNexusAppWindow
    // can't detect the in-flight window, so a second click during that gap
    // spawns a second Edge. Time-only guard: PID liveness is unreliable
    // because Edge's launcher process exits within ~30ms of forking the
    // actual browser.
    private static readonly object _spawnLock = new();
    private static DateTime _lastSpawnUtc = DateTime.MinValue;
    private static readonly TimeSpan SpawnSettleWindow = TimeSpan.FromSeconds(4);
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

    /// <summary>
    /// Wire the "Start at logon" menu toggle. When both callbacks are non-null
    /// the tray menu shows a checkable item that flips HKCU\Run autostart.
    /// </summary>
    public static void ConfigureAutostart(Action onToggle, Func<bool> isEnabled)
    {
        _onToggleAutostart = onToggle;
        _isAutostartEnabled = isEnabled;
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

    /// <summary>
    /// Wire the "Profiles" submenu. <paramref name="getProfiles"/> is called on
    /// each right-click; <paramref name="onSwitchProfile"/> is called with the
    /// selected profile id. Both run on the tray window thread; no locking needed.
    /// </summary>
    public static void ConfigureProfiles(
        Func<(System.Collections.Generic.IReadOnlyList<(string Id, string Name)> Items, string ActiveId)> getProfiles,
        Action<string> onSwitchProfile)
    {
        _getProfiles = getProfiles;
        _onSwitchProfile = onSwitchProfile;
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
            if (_requestedVisible)
            {
                if (!_visible)
                {
                    var nid = _nid;
                    if (Shell_NotifyIcon(NIM_ADD, ref nid))
                    {
                        _visible = true;
                        StopIconRetry();
                        DiagFile("tray icon added");
                    }
                    else
                    {
                        // Shell tray not ready to accept the icon (heavy cold
                        // boot, or a taskbar recreation in flight). Leave
                        // _visible=false so a TaskbarCreated broadcast, the
                        // service's connect-time push, or the retry poll
                        // re-attempts; latching it true here is what stranded
                        // the icon invisible until a manual off/on toggle.
                        DiagFile("NIM_ADD failed; shell tray not ready");
                        StartIconRetry();
                    }
                }
            }
            else
            {
                StopIconRetry();
                if (_visible)
                {
                    var nid = _nid;
                    Shell_NotifyIcon(NIM_DELETE, ref nid);
                    _visible = false;
                }
            }
        }
        catch { /* tray is non-critical */ }
    }

    // Re-attempt NIM_ADD on a cadence when the shell tray rejected it on a cold
    // boot: the taskbar exists but isn't ready, so no TaskbarCreated broadcast
    // follows to drive a re-add. SetTimer/KillTimer must run on the window
    // thread; an off-thread caller (the service's cross-thread SetVisible push)
    // posts a wake so the window thread re-attempts and arms on its own.
    private static void StartIconRetry()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!ReferenceEquals(Thread.CurrentThread, _thread))
        {
            PostMessage(_hwnd, (uint)WM_ARM_ICON_RETRY, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        if (_retryTimerArmed) return;
        _iconRetryAttempts = 0;
        if (SetTimer(_hwnd, (UIntPtr)IconRetryTimerId, IconRetryIntervalMs, IntPtr.Zero) != UIntPtr.Zero)
        {
            _retryTimerArmed = true;
        }
    }

    private static void StopIconRetry()
    {
        // KillTimer must run on the timer-owning thread. A cross-thread hide
        // (service push of ShowWindowsTrayIcon=false) skips this; the next
        // WM_TIMER tick on the window thread sees !_requestedVisible and stops.
        if (_thread is null || !ReferenceEquals(Thread.CurrentThread, _thread)) return;
        if (!_retryTimerArmed || _hwnd == IntPtr.Zero) return;
        KillTimer(_hwnd, (UIntPtr)IconRetryTimerId);
        _retryTimerArmed = false;
    }

    private static void Run()
    {
        try
        {
            DiagFile("TrayIcon.Run() entered");
            _pinnedProc = Proc;

            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _pinnedProc,
                lpszClassName = "NexusTrayWnd",
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
            _hwnd = hwnd;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

            // Opt the process into Windows 11 immersive theming so the
            // right-click popup menu picks up the system dark/light theme.
            ApplyImmersiveTheme();

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
            nid.szTip = "Nexus";
            // ByValTStr fields must be non-null before marshaling.
            nid.szInfo = string.Empty;
            nid.szInfoTitle = string.Empty;

            lock (_sync)
            {
                _nid = nid;
                _iconDataReady = true;
                ApplyIconVisibilityNoThrow();
            }
            DiagFile($"tray ready hwnd=0x{hwnd.ToInt64():X} hIcon=0x{hIcon.ToInt64():X}");

            // Message pump
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            lock (_sync)
            {
                StopIconRetry();
                if (_visible)
                {
                    var deleteNid = _nid;
                    Shell_NotifyIcon(NIM_DELETE, ref deleteNid);
                }
                _visible = false;
                _iconDataReady = false;
                _hwnd = IntPtr.Zero;
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
                _retryTimerArmed = false;
                _hwnd = IntPtr.Zero;
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
            _retryTimerArmed = false;
            _hwnd = IntPtr.Zero;
            _thread = null;
        }
    }

    private static void DiagFile(string msg)
    {
        // Direct FileStream write to a known absolute path. C:\Users\Public
        // is world-writable, no env-var resolution needed.
        try
        {
            using var fs = new System.IO.FileStream(
                @"C:\Users\Public\nexus-tray-debug.log",
                System.IO.FileMode.Append,
                System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [TrayIcon p{System.Diagnostics.Process.GetCurrentProcess().Id}] {msg}\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                var ev = lParam.ToInt32() & 0xFFFF;
                DiagFile($"tray ev=0x{ev:X4}");

                if (ev == WM_RBUTTONUP)
                {
                    POINT pt;
                    GetCursorPos(out pt);
                    // Re-apply on every popup so theme changes the user makes
                    // in Settings flow through without restarting the service.
                    ApplyImmersiveTheme();
                    var menu = CreatePopupMenu();
                    AppendMenu(menu, MF_STRING, IDM_OPEN_APP, "Open");
                    AppendMenu(menu, MF_STRING, IDM_OPEN_SETTINGS, "Settings");
                    _profileMenuIds.Clear();
                    if (_getProfiles is not null)
                    {
                        try
                        {
                            var (items, activeId) = _getProfiles();
                            if (items.Count >= 1)
                            {
                                var sub = CreatePopupMenu();
                                for (var i = 0; i < items.Count; i++)
                                {
                                    var (id, name) = items[i];
                                    var flags = MF_STRING | (id == activeId ? MF_CHECKED : MF_UNCHECKED);
                                    AppendMenu(sub, flags, IDM_PROFILE_BASE + i, name);
                                    _profileMenuIds.Add(id);
                                }
                                AppendMenu(menu, MF_POPUP, sub, "Profiles");
                            }
                        }
                        catch { /* omit submenu on any fetch failure */ }
                    }
                    AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
                    AppendMenu(menu, MF_STRING, IDM_SHUTDOWN, "Shut down");
                    SetForegroundWindow(hwnd);
                    TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
                    DestroyMenu(menu);
                }
                else if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK)
                {
                    // Single OR double left-click opens the window.
                    OpenLocalWindow();
                }
                else if (ev == NIN_BALLOONUSERCLICK)
                {
                    // A balloon was clicked. NIN_BALLOONUSERCLICK carries no
                    // identity, so routing state lives in _noticeFolderPath:
                    // transfer notices open their inbox folder, everything
                    // else (pairing) opens the dashboard — whose snapshot
                    // provider replays a pending pair request so the
                    // Allow/Deny modal pops once the WebSocket subscribes.
                    string? folder;
                    lock (_sync)
                    {
                        folder = _noticeFolderPath;
                        _noticeFolderPath = null;
                        _balloonKind = BalloonKind.None;
                    }
                    if (folder is not null && System.IO.Directory.Exists(folder))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = folder,
                            UseShellExecute = true,
                        });
                    }
                    else
                    {
                        OpenLocalWindow();
                    }
                }
            }
            else if (msg == WM_DISPLAYCHANGE)
            {
                DisplayChanged?.Invoke();
            }
            else if (msg == WM_COMMAND)
            {
                var id = wParam.ToInt32() & 0xFFFF;
                if (id == IDM_OPEN_APP)
                {
                    OpenLocalWindow();
                }
                else if (id == IDM_OPEN_SETTINGS)
                {
                    OpenLocalWindow(servicePort: 0, path: "/settings");
                }
                else if (id == IDM_SHUTDOWN)
                {
                    // Defensive try: _onExit ends in Environment.Exit so the
                    // happy path doesn't return; only a throw inside the
                    // pre-Exit cleanup (panel close, overlay stop) would land
                    // here. We swallow because there is nothing useful to do.
                    try { _onExit?.Invoke(); } catch { }
                }
                else if (id >= IDM_PROFILE_BASE && id < IDM_PROFILE_BASE + _profileMenuIds.Count)
                {
                    var profileId = _profileMenuIds[id - IDM_PROFILE_BASE];
                    try { _onSwitchProfile?.Invoke(profileId); } catch { }
                }
            }
            else if (msg == WM_ARM_ICON_RETRY)
            {
                // Off-thread add failed; re-attempt here, which arms the retry
                // timer on this (the window) thread if it still can't add.
                lock (_sync) { ApplyIconVisibilityNoThrow(); }
            }
            else if (msg == WM_TIMER && wParam == (IntPtr)IconRetryTimerId)
            {
                lock (_sync)
                {
                    _iconRetryAttempts++;
                    if (!_requestedVisible || _visible)
                    {
                        StopIconRetry();
                    }
                    else if (_iconRetryAttempts >= IconRetryMaxAttempts)
                    {
                        DiagFile($"NIM_ADD still failing after {_iconRetryAttempts} attempts; giving up");
                        StopIconRetry();
                    }
                    else
                    {
                        ApplyIconVisibilityNoThrow();
                    }
                }
            }
            else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                // Taskbar (re)created: the prior NIM_ADD registration is gone,
                // so re-add. Covers Explorer restarts and the cold-boot case
                // where the shell wasn't ready when the icon first went up.
                DiagFile("TaskbarCreated received; re-adding tray icon");
                lock (_sync)
                {
                    _visible = false;
                    ApplyIconVisibilityNoThrow();
                }
            }
        }
        catch { /* don't let WndProc crash kill the service */ }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OpenDashboard(int servicePort = 0, string path = "/")
    {
        var port = ResolveDashboardPort(servicePort);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = $"http://localhost:{port}{path}",
            UseShellExecute = true,
        });
    }

    public static void OpenLocalWindow(int servicePort = 0, string path = "/")
    {
        var port = ResolveDashboardPort(servicePort);
        DiagFile($"OpenLocalWindow port={port} path={path}");

        lock (_spawnLock)
        {
            // Spawn-in-flight guard: if we started an Edge --app less than
            // SpawnSettleWindow ago, drop this click. We rely on time only,
            // NOT on whether the launcher PID is still alive: Edge does a
            // multi-process dance where the launcher exits within ~30ms
            // after forking the actual browser process, so PID-liveness is
            // false within milliseconds of a real successful spawn.
            if (_lastSpawnUtc != DateTime.MinValue && DateTime.UtcNow - _lastSpawnUtc < SpawnSettleWindow)
            {
                DiagFile($"spawn in flight (age={(DateTime.UtcNow - _lastSpawnUtc).TotalMilliseconds:F0}ms); dropping click");
                return;
            }

            // Preferred path: the nexus-overlay process hosts a WebView2
            // dashboard window that shares the Chromium process tree with
            // the overlay widgets - far cheaper than spawning a fresh
            // Edge --app tree. Sending the registered ShowDashboard
            // message either creates or focuses the dashboard window.
            var overlayMsg = path == "/settings"
                ? ShowDashboardSettingsMessageName
                : ShowDashboardMessageName;
            if (TrySendShowDashboardToOverlay(messageName: overlayMsg))
            {
                DiagFile($"{overlayMsg} posted to nexus-overlay marshaler");
                return;
            }

            // Overlay process not running - try to start it before falling
            // back to the heavy Edge --app path. EnsureOverlayRunning is
            // best-effort; on success the marshaler usually appears within
            // a few seconds. We retry the send once after the spawn.
            if (EnsureOverlayRunning() && TrySendShowDashboardToOverlay(timeoutMs: 8000, messageName: overlayMsg))
            {
                _lastSpawnUtc = DateTime.UtcNow;
                DiagFile($"started nexus-overlay and posted {overlayMsg}");
                return;
            }

            DiagFile("nexus-overlay unreachable, falling back to Edge --app");

            // Fallback: legacy Edge --app spawn. Only reached when the
            // overlay binary is missing or refuses to start.
            var existing = FindExistingNexusAppWindow();
            if (existing != IntPtr.Zero)
            {
                FocusWindow(existing);
                DiagFile($"focused existing msedge --app window 0x{existing.ToInt64():X}");
                return;
            }

            var edgePath = FindEdge();
            if (edgePath is null)
            {
                DiagFile("Edge not found, falling back to default browser");
                OpenDashboard(port, path);
                return;
            }

            var url = $"http://localhost:{port}{path}";
            // Isolated profile dir under ProgramData so Authenticated Users can
            // read/write it without a roaming-profile redirect. Mirrors the
            // panel-kiosk launcher's location convention.
            var userDataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Nexus", "DashboardEdge");
            try { System.IO.Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add($"--app={url}");
            psi.ArgumentList.Add("--new-window");
            // Isolated profile - cuts Edge sync, extensions, identity, sidebar,
            // Copilot, Workspaces, and the rest of the user-profile baggage
            // that the --app would otherwise drag in from the default profile.
            psi.ArgumentList.Add($"--user-data-dir={userDataDir}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--disable-features=msEdgeSidebarV2,msEdgeSplitWindow,msEdgeWorkspaces,msEdgeJSONViewer");
            psi.ArgumentList.Add("--disable-sync");
            psi.ArgumentList.Add("--disable-extensions");
            psi.ArgumentList.Add("--disable-background-mode");
            psi.ArgumentList.Add("--disable-background-networking");
            psi.ArgumentList.Add("--disable-default-apps");
            psi.ArgumentList.Add("--disable-notifications");
            psi.ArgumentList.Add("--disable-session-crashed-bubble");
            psi.ArgumentList.Add("--disable-infobars");
            try
            {
                var p = System.Diagnostics.Process.Start(psi);
                _lastSpawnUtc = DateTime.UtcNow;
                DiagFile($"Edge --app spawned pid={p?.Id.ToString() ?? "null"}");
            }
            catch (Exception ex)
            {
                DiagFile($"Edge --app failed: {ex.Message}, falling back to default browser");
                OpenDashboard(port);
            }
        }
    }


    /// <summary>
    /// Close any open standalone Nexus --app window (the Edge --app shell
    /// hosting the dashboard). Best-effort, fire-and-forget: posts WM_CLOSE
    /// to the HWND found by <see cref="FindExistingNexusAppWindow"/> and
    /// returns immediately - Edge processes the close on its own message
    /// loop ms later. No-op when no such window exists.
    ///
    /// Used by the tray "Shut down" handler and by the service's
    /// ApplicationStopping hook so that quitting Nexus always tears the
    /// window down, matching the settings "Stop Nexus" UX path (which closes
    /// the window via window.close() from the React side).
    /// </summary>
    public static void CloseAppWindow()
    {
        try
        {
            var hwnd = FindExistingNexusAppWindow();
            if (hwnd != IntPtr.Zero)
            {
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                DiagFile($"posted WM_CLOSE to Nexus --app window 0x{hwnd.ToInt64():X}");
            }
            // Also post the same message to the nexus-overlay dashboard
            // window if it's currently visible. We don't kill the overlay
            // process - the dashboard window's WM_CLOSE handler just hides
            // it so reopen stays instant.
            var dashHwnd = FindWindow(OverlayDashboardClassName, null);
            if (dashHwnd != IntPtr.Zero)
            {
                PostMessage(dashHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                DiagFile($"posted WM_CLOSE to nexus-overlay dashboard 0x{dashHwnd.ToInt64():X}");
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Show a native tray balloon for an incoming phone pair request. Runs
    /// in the user session (the helper process in service mode, or the
    /// interactive host) — the only place a notification can surface.
    /// Clicking the balloon routes through NIN_BALLOONUSERCLICK to
    /// <see cref="OpenLocalWindow"/>. Best-effort: a no-op when the tray icon
    /// isn't currently shown, since the balloon needs the icon to anchor to
    /// (the service's snapshot provider still surfaces the request the next
    /// time the dashboard is opened).
    /// </summary>
    public static void ShowPairBalloon(string deviceLabel)
    {
        var text = string.IsNullOrWhiteSpace(deviceLabel)
            ? "A phone wants to connect. Click to review."
            : $"{deviceLabel} wants to connect. Click to review.";
        ModifyBalloon("Nexus pairing request", text, BalloonKind.Pair, folderPath: null);
    }

    /// <summary>
    /// Dismiss the pairing balloon once the request is resolved. Only clears
    /// when a pairing balloon owns the slot — every resolution fires this, and
    /// it must not blank a live transfer notice.
    /// </summary>
    public static void ClearPairBalloon()
        => ModifyBalloon(string.Empty, string.Empty, BalloonKind.None, folderPath: null, onlyIfKind: BalloonKind.Pair);

    /// <summary>
    /// One balloon slot exists (NIM_MODIFY replaces), so clicks carry no
    /// identity; the owner kind + optional folder route NIN_BALLOONUSERCLICK.
    /// </summary>
    private enum BalloonKind { None, Pair, Notice, UpdateReady }
    private static BalloonKind _balloonKind;
    private static string? _noticeFolderPath;

    /// <summary>
    /// Generic one-shot balloon (incoming transfers, future notices). Same
    /// best-effort semantics as <see cref="ShowPairBalloon"/>: no-op while the
    /// tray icon is hidden — and while a pairing balloon is pending, which is
    /// time-sensitive and must not lose its click routing to a notice.
    /// </summary>
    public static void ShowNoticeBalloon(string title, string text, string? folderPath)
    {
        var folder = string.IsNullOrEmpty(folderPath) ? null : folderPath;
        // The click hint is appended here, not by the notice producer — other
        // platforms' notifications (macOS osascript) have no click action.
        if (folder is not null)
        {
            text = $"{text} Click to open the folder.";
        }
        ModifyBalloon(title, text, BalloonKind.Notice, folder, notWhileKind: BalloonKind.Pair);
    }

    /// <summary>
    /// Notify the user that a staged update is ready to install. Clicking the
    /// balloon opens the dashboard where the install button is presented.
    /// No-op while a pairing balloon is pending (pairing is time-sensitive).
    /// </summary>
    public static void ShowUpdateReadyBalloon(string version)
    {
        ModifyBalloon(
            $"Nexus {version} is ready",
            "Click to install the update.",
            BalloonKind.UpdateReady,
            folderPath: null,
            notWhileKind: BalloonKind.Pair);
    }

    private static void ModifyBalloon(
        string title,
        string text,
        BalloonKind kind,
        string? folderPath,
        BalloonKind? onlyIfKind = null,
        BalloonKind? notWhileKind = null)
    {
        lock (_sync)
        {
            if (!_iconDataReady || !_visible)
            {
                return;
            }
            if (onlyIfKind is { } only && _balloonKind != only)
            {
                return;
            }
            if (notWhileKind is { } blocked && _balloonKind == blocked)
            {
                return;
            }
            // Routing state and the visible balloon swap under one acquisition,
            // so a click can never observe one without the other.
            _balloonKind = kind;
            _noticeFolderPath = folderPath;
            try
            {
                var nid = _nid;
                nid.uFlags = NIF_INFO;
                nid.szInfo = text ?? string.Empty;
                nid.szInfoTitle = title ?? string.Empty;
                nid.dwInfoFlags = NIIF_INFO;
                Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            catch { /* tray is non-critical */ }
        }
    }

    private const string OverlayMarshalerClassName = "Nexus.Overlay.Marshaler";
    private const string OverlayDashboardClassName = "Nexus.Overlay.Dashboard";
    private const string ShowDashboardMessageName = "Nexus.Overlay.ShowDashboard";
    // Overlay-side handler for this message navigates directly to /settings.
    private const string ShowDashboardSettingsMessageName = "Nexus.Overlay.ShowDashboardSettings";

    /// <summary>
    /// Tries to deliver a registered window message to the running nexus-overlay
    /// process's marshaler window. Returns false if no marshaler is found
    /// within <paramref name="timeoutMs"/> (default: 0, i.e. one-shot
    /// check; pass a positive value after spawning the overlay to give it
    /// time to register its window).
    /// </summary>
    /// <param name="messageName">Registered window message name (e.g.
    /// <c>"Nexus.Overlay.ShowDashboard"</c>, <c>"Nexus.Overlay.ShowPanelKiosk"</c>).</param>
    /// <param name="handoffForeground">When true, calls
    /// <c>AllowSetForegroundWindow</c> on the overlay process before posting
    /// so it can raise/focus its window. Set for click-driven launches
    /// (ShowDashboard); leave false for background triggers like the kiosk
    /// auto-launch where there's no foreground privilege to hand off.</param>
    internal static bool TryPostToOverlayMarshaler(string messageName, int timeoutMs = 0, bool handoffForeground = false)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            IntPtr marshaler;
            while (true)
            {
                marshaler = FindWindow(OverlayMarshalerClassName, null);
                if (marshaler != IntPtr.Zero) break;
                if (DateTime.UtcNow >= deadline) return false;
                System.Threading.Thread.Sleep(150);
            }
            var msg = RegisterWindowMessage(messageName);
            if (msg == 0)
            {
                DiagFile($"RegisterWindowMessage({messageName}) failed");
                return false;
            }
            if (handoffForeground)
            {
                // SetForegroundWindow only works for the process that owns
                // the current foreground. The caller (e.g., tray click) is
                // foreground; hand the privilege to the overlay so its
                // imminent SetForeground call lands.
                try
                {
                    GetWindowThreadProcessId(marshaler, out var overlayPid);
                    if (overlayPid != 0) AllowSetForegroundWindow(overlayPid);
                }
                catch { /* worst case is unfocused window */ }
            }

            var ok = PostMessage(marshaler, msg, IntPtr.Zero, IntPtr.Zero);
            if (!ok) DiagFile($"PostMessage({messageName}) to 0x{marshaler.ToInt64():X} failed");
            return ok;
        }
        catch (Exception ex)
        {
            DiagFile($"TryPostToOverlayMarshaler({messageName}): {ex.Message}");
            return false;
        }
    }

    private static bool TrySendShowDashboardToOverlay(int timeoutMs = 0, string messageName = ShowDashboardMessageName)
        => TryPostToOverlayMarshaler(messageName, timeoutMs, handoffForeground: true);

    /// <summary>
    /// Starts nexus-overlay.exe in the current user session. We're already
    /// running in the interactive session via the schtasks-hopped
    /// <c>Nexus.exe --open-app</c>, so a plain Process.Start is sufficient
    /// (no cross-session CreateProcessAsUser dance). Returns true on
    /// successful Process.Start, false if the binary is missing or the
    /// start fails.
    /// </summary>
    private static bool EnsureOverlayRunning()
    {
        try
        {
            // First check is cheap: if any nexus-overlay.exe is alive in
            // the current user's session, just wait for its marshaler.
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("nexus-overlay"))
            {
                p.Dispose();
                return true;
            }

            var serviceDir = System.AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(serviceDir)) return false;
            var hostPath = System.IO.Path.Combine(serviceDir, "overlay", "nexus-overlay.exe");
            if (!System.IO.File.Exists(hostPath))
            {
                DiagFile($"nexus-overlay.exe not found at {hostPath}");
                return false;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(hostPath)!,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            DiagFile($"spawned nexus-overlay pid={proc?.Id.ToString() ?? "null"}");
            return proc is not null;
        }
        catch (Exception ex)
        {
            DiagFile($"EnsureOverlayRunning: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the MainWindowHandle of any currently-running msedge.exe
    /// whose window title starts with "Nexus". Avoids the FindExistingAppWindow
    /// trap of matching by title alone across ALL top-level windows (which
    /// could pick up File Explorer or stale handles). Internal: the native
    /// file dialog owns itself to this window so it opens over the app.
    /// </summary>
    internal static IntPtr FindExistingNexusAppWindow()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("msedge"))
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var title = p.MainWindowTitle;
                    if (!string.IsNullOrEmpty(title) &&
                        title.StartsWith("Nexus", StringComparison.OrdinalIgnoreCase))
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch { /* process exited mid-iteration */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* best-effort */ }
        return IntPtr.Zero;
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
            // (nexus-overlay.exe) carry WS_EX_TOOLWINDOW so they don't
            // show in Alt-Tab; they also have a title that starts with
            // "Nexus", which would otherwise match here. The dashboard
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
            // caption. Our SPA is titled "Nexus"; match any window that
            // starts with that so we still catch "Nexus - <section>"
            // style titles if we ever add them.
            if (title.StartsWith("Nexus", StringComparison.OrdinalIgnoreCase))
            {
                result = hwnd;
                return false; // stop enumeration
            }
            return true;
        };
        EnumWindows(_pinnedEnumProc, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Opts the process into the system's dark/light app theme so that popup
    /// menus drawn via TrackPopupMenu render with Windows 11 immersive
    /// colors (dark menu surface, light text, accent highlight) instead of
    /// the legacy classic-theme white menu. Process-wide state, not
    /// per-window. Safe to call repeatedly. Uses undocumented uxtheme.dll
    /// ordinals stable since Windows 10 1903; the same approach File
    /// Explorer and Notepad use.
    /// </summary>
    private static void ApplyImmersiveTheme()
    {
        // Build gate: the int-taking SetPreferredAppMode only exists on
        // 1903+. Calling on 1809 binds to the older BOOL-taking
        // AllowDarkModeForApp and coerces APPMODE_ALLOW_DARK (1) to TRUE; that
        // coercion is coincidental, not guaranteed, so skip on older builds.
        if (Environment.OSVersion.Version.Build < SetPreferredAppModeMinBuild)
        {
            return;
        }

        try
        {
            // ALLOW_DARK lets popup menus follow the user's Apps light/dark
            // preference; DEFAULT clears any prior allow so light-mode users
            // get the standard light menu. We avoid FORCE_* so we never
            // override the user's per-process app-mode preference.
            var mode = IsSystemDarkMode() ? APPMODE_ALLOW_DARK : APPMODE_DEFAULT;
            try { SetPreferredAppMode(mode); } catch { }
            try { FlushMenuThemes(); } catch { }
        }
        catch { /* theming is non-critical */ }
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                return v == 0;
            }
        }
        catch { }
        return false;
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

    // Full modern NOTIFYICONDATAW. The balloon fields (szInfo/szInfoTitle/
    // dwInfoFlags) only marshal correctly when the struct — and the cbSize
    // derived from it via Marshal.SizeOf — covers them. The extra fields are
    // inert for the existing NIM_ADD/DELETE/icon-only paths.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
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
    // MF_POPUP overload: idNewItem is the submenu HMENU, which is pointer-sized.
    [DllImport("user32", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr idNewItem, string text);
    [DllImport("user32")] private static extern bool TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32")] private static extern bool AllowSetForegroundWindow(uint dwProcessId);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32")] private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32")] private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

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

    // Windows 11 immersive theming for popup menus. uxtheme ordinals 135 +
    // 136 are undocumented but have been ABI-stable since Windows 10 1903
    // and are used by Windows itself.
    [DllImport("uxtheme", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme", EntryPoint = "#136", SetLastError = false)]
    private static extern void FlushMenuThemes();
}
