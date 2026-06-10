#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Brings windows the helper spawns (native file dialogs, Explorer folder
/// windows) in front of the Nexus app window. A background process can't
/// simply SetForegroundWindow (foreground lock), so this attaches to the
/// current foreground thread's input queue first — the standard escape hatch
/// for "the user just asked for this window via another process's UI".
/// </summary>
[SupportedOSPlatform("windows")]
public static class ForegroundNudge
{
    public static void TryForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            var fg = GetForegroundWindow();
            uint fgThread = 0;
            if (fg != IntPtr.Zero)
            {
                fgThread = GetWindowThreadProcessId(fg, out _);
            }

            var cur = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(cur, fgThread, false);
                }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Open <paramref name="dir"/> in Explorer and bring the new window to the
    /// front (Explorer windows spawned by a background process open behind the
    /// app otherwise). If Explorer reuses an already-open window for the
    /// folder, no new window appears and the nudge quietly gives up.
    /// </summary>
    public static void OpenFolderOverApp(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var before = SnapshotExplorerWindows();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });

            // The window is created asynchronously by the (foreign) Explorer
            // process — there is no completion signal to wait on, so a bounded
            // poll finds the freshly-created folder window.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var fresh = SnapshotExplorerWindows().FirstOrDefault(h => !before.Contains(h));
                    if (fresh != IntPtr.Zero)
                    {
                        TryForeground(fresh);
                        return;
                    }

                    Thread.Sleep(100);
                }
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Watch for the first visible window created on <paramref name="threadId"/>
    /// (the file dialog on its STA thread) and bring it to the front. Runs on
    /// the thread pool; the dialog thread itself is blocked inside Show().
    /// </summary>
    public static void ForegroundThreadWindowWhenShown(uint threadId)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var i = 0; i < 40; i++)
            {
                var found = IntPtr.Zero;
                EnumThreadWindows(threadId, (h, _) =>
                {
                    if (IsWindowVisible(h))
                    {
                        found = h;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    TryForeground(found);
                    return;
                }

                Thread.Sleep(100);
            }
        });
    }

    private static HashSet<IntPtr> SnapshotExplorerWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            if (GetClassNameW(h, sb, 64) > 0)
            {
                var cls = sb.ToString();
                if (cls is "CabinetWClass" or "ExploreWClass")
                {
                    set.Add(h);
                }
            }

            return true;
        }, IntPtr.Zero);
        return set;
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attached);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();
}
#endif
