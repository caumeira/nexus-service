using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

public enum RelaunchResult
{
    Started,
    AlreadyElevated,
    Unsupported,
    UserDenied,
    Failed,
}

/// <summary>
/// Re-launches the current process with a UAC elevation prompt. Used by the
/// dashboard "Restart as administrator" button when the service is running
/// non-elevated and the user wants the privileged hardware features back.
///
/// Mirrors the runas spawn pattern in PawnIoInstaller. The new elevated child
/// binds the same port (9400); we exit the current instance after a short
/// delay so the response flushes and the listener releases before the child
/// tries to bind.
/// </summary>
public static class ProcessRelauncher
{
    public static RelaunchResult TryRelaunchAsAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return RelaunchResult.Unsupported;
        }

        var elevation = ProcessElevation.GetCurrent();
        if (!elevation.Supported)
        {
            return RelaunchResult.Unsupported;
        }
        if (elevation.IsElevated)
        {
            return RelaunchResult.AlreadyElevated;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            return RelaunchResult.Failed;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            // Tell the elevated child to wait for our mutex + port to free up
            // before binding. Without this the child sees the still-held
            // single-instance mutex, treats us as "already running", opens
            // the dashboard window and exits without taking over.
            Arguments = "--relaunch-elevated",
            Verb = "runas",
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                return RelaunchResult.Failed;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return RelaunchResult.UserDenied;
        }
        catch
        {
            return RelaunchResult.Failed;
        }

        // Exit shortly after returning so the HTTP response can flush. The
        // child polls the mutex with a 10s timeout (see Program.cs handling
        // of --relaunch-elevated), so a tight exit window here is safe even
        // when the UAC prompt sits open longer.
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Environment.Exit(0);
        });

        return RelaunchResult.Started;
    }
}
