using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qos.Service.Panel;

/// <summary>
/// Spawns and supervises qos-overlay.exe, the WebView2 host that
/// renders floating widgets on the Windows desktop. The host lives in the
/// `overlay/` subdirectory next to the service exe; that layout is enforced
/// by the PublishOverlayHost MSBuild target so the WinForms self-contained
/// payload (CLR + WebView2 native loader) cannot collide with the
/// AOT-native service root.
///
/// Lifecycle:
/// - Started at service boot when <c>UiSettings.OverlayWidgetsEnabled</c>
///   is true (set automatically the first time the user pins a widget).
/// - Restarted on crash with simple linear backoff.
/// - Killed on service shutdown.
/// </summary>
public sealed class PanelOverlayHostLauncher : IOverlayHost
{
    /// <summary>
    /// Always-on-top is pushed to the Windows sidecar via the SPA WebMessage
    /// bridge and via a 5s prefs poll. The service-side abstraction is a
    /// no-op so tray and reconcile callers can target IOverlayHost uniformly.
    /// </summary>
    public void SetAlwaysOnTop(bool value) { }

    private Process? _process;
    private DateTime _lastSpawnUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private IntPtr _jobHandle = IntPtr.Zero;
    private readonly object _lock = new();
    /// <summary>
    /// Stop() sets this true so a queued OnExited callback - already on
    /// the threadpool when Stop ran - won't respawn the host. Re-armed on
    /// the next Start().
    /// </summary>
    private volatile bool _stopRequested;

    private static readonly string PidFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "qOS", "panel-desktop-pid.txt");

    public PanelOverlayHostLauncher()
    {
        // Create a Windows Job Object with KILL_ON_JOB_CLOSE so any
        // child we assign to it is killed when this handle closes - i.e.
        // when the service process exits, including via taskkill /F or
        // a hard crash that ApplicationStopping can't react to.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { _jobHandle = CreateChildKillJob(); }
            catch (Exception ex) { Console.Error.WriteLine($"[overlay-host] job-object init failed: {ex.Message}"); }
        }
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Start the host process if it isn't already running. No-ops on
    /// non-Windows. Returns true if the process is alive after the call.
    /// </summary>
    public bool Start()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (IsRunning) return true;

        var hostPath = ResolveHostPath();
        if (hostPath is null || !File.Exists(hostPath))
        {
            Console.Error.WriteLine($"[overlay-host] qos-overlay.exe not found at expected path '{hostPath ?? "<null>"}'; the PublishOverlayHost target must populate <publish>/overlay/. Desktop widgets disabled.");
            return false;
        }

        try
        {
            _stopRequested = false;
            _lastSpawnUtc = DateTime.UtcNow;
            var psi = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // cwd must be the overlay/ subdir so WebView2Loader.dll
                // resolves alongside the host exe.
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            };
            _process = Process.Start(psi);
            if (_process is not null)
            {
                WritePidFile(_process.Id);
                _process.EnableRaisingEvents = true;
                _process.Exited += OnExited;
                // Bind to the kill-on-close job so the host dies with us.
                if (_jobHandle != IntPtr.Zero)
                {
                    try
                    {
                        AssignProcessToJobObject(_jobHandle, _process.Handle);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[overlay-host] job-object assign failed: {ex.Message}");
                    }
                }
                Console.WriteLine($"[overlay-host] started pid {_process.Id}");
            }
            return IsRunning;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-host] failed to start: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        // Latch the stop intent BEFORE attempting Kill so that a queued
        // OnExited (already on the threadpool) sees it and skips respawn.
        _stopRequested = true;
        var proc = _process;
        _process = null;
        if (proc is not null)
        {
            try { proc.Exited -= OnExited; } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                // Access denied / process already gone / job-object death
                // races us. Log so a leaked host shows up in service logs
                // and the next reconcile pass can deal with it.
                Console.Error.WriteLine($"[overlay-host] kill failed: {ex.Message}");
            }
            try { proc.Dispose(); } catch { }
        }
        try { DeletePidFile(); } catch { }
    }

    /// <summary>
    /// Kill any orphaned host process from a previous crash. Called on
    /// service startup before spawning a new instance. Mirrors
    /// <see cref="PanelKioskLauncher.CleanupOrphans"/>.
    /// </summary>
    public static void CleanupOrphans()
    {
        try
        {
            if (!File.Exists(PidFilePath)) return;
            var text = File.ReadAllText(PidFilePath).Trim();
            if (!int.TryParse(text, out var pid)) { DeletePidFile(); return; }
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc.ProcessName.Contains("qos-overlay", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                    Console.WriteLine($"[overlay-host] killed orphan (pid {pid})");
                }
                proc.Dispose();
            }
            catch { }
            DeletePidFile();
        }
        catch { }
    }

    private static string? ResolveHostPath()
    {
        // AppContext.BaseDirectory is single-file-safe and AOT-safe; both
        // Process.MainModule.FileName and Assembly.Location have edge cases
        // under publish modes we use.
        var serviceDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(serviceDir)) return null;
        return Path.Combine(serviceDir, "overlay", "qos-overlay.exe");
    }

    private void OnExited(object? sender, EventArgs e)
    {
        // Stop() was called; this callback was already in flight on the
        // threadpool when Stop ran (handler unsubscribe doesn't drain
        // pending invocations). Don't respawn.
        if (_stopRequested) return;

        // Linear backoff cap: don't restart more than 3 times in a row
        // within 30s. Prevents tight crash-loop hammering.
        var since = DateTime.UtcNow - _lastSpawnUtc;
        if (since < TimeSpan.FromSeconds(30))
        {
            _consecutiveFailures++;
            if (_consecutiveFailures > 3)
            {
                Console.Error.WriteLine($"[overlay-host] giving up after {_consecutiveFailures} rapid failures");
                return;
            }
        }
        else
        {
            _consecutiveFailures = 0;
        }
        Console.WriteLine($"[overlay-host] exited (code {_process?.ExitCode}); restarting");
        _process = null;
        // Give the host a moment before respawning. Run on the default
        // scheduler with explicit error handling so a Start() throw is
        // reported instead of disappearing into an unobserved task.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(2000);
                if (_stopRequested) return;
                Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-host] respawn task failed: {ex.Message}");
            }
        });
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
        try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { }
    }

    // ── Windows Job Object plumbing ──
    // CreateJobObject + JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE makes every
    // process assigned to the job die when this handle closes (= when
    // qOS.exe exits, including taskkill /F or hard crash).

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private static IntPtr CreateChildKillJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        return handle;
    }
}
