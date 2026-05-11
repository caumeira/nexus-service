#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Canonical install / uninstall primitive for Qos as a Windows Service.
/// Both the Inno installer and a bare-EXE self-install invoke
/// <see cref="RunInstall"/> / <see cref="RunUninstall"/>.
///
/// Service identity: <c>QosService</c>, running as <c>LocalSystem</c>,
/// start type <c>Automatic</c>, depends on the PawnIO kernel driver.
///
/// AOT-friendly: shells out to sc.exe / netsh.exe / pnputil.exe rather than
/// using <c>System.ServiceProcess</c> (which is reflection-heavy and not
/// well-suited to AOT).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceInstaller
{
    public const string ServiceName = "QosService";
    public const string ServiceDisplayName = "Qos Service";
    public const string ServiceDescription = "Qos hardware monitoring and control";
    public const string InstallDirName = "Qos";
    public const string BinaryName = "Qos.exe";
    public const string FirewallRuleName = "QosService";
    public const string UninstallRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Qos";
    public const int DefaultPort = 9400;

    /// <summary>
    /// `Qos.exe --install` entry. Self-elevates if needed; copies files to
    /// %ProgramFiles%\Qos\ if invoked from elsewhere; registers the Windows
    /// Service; installs PawnIO; opens the firewall; starts the service.
    /// Add/Remove Programs registration is owned by the Inno Setup wrapper
    /// (the {AppId}_is1 key), not by this method.
    /// </summary>
    public static int RunInstall(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[install] --install is Windows-only");
            return 1;
        }

        if (!Platform.ProcessElevation.GetCurrent().IsElevated)
        {
            return SelfElevateAndReinvoke("--install");
        }

        try
        {
            var sourceExe = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("can't resolve own EXE path");
            var sourceDir = Path.GetDirectoryName(sourceExe)
                ?? throw new InvalidOperationException("can't resolve own EXE dir");
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                InstallDirName);

            // 1. Copy payload to install dir if we're running from elsewhere.
            // Already in install dir = idempotent re-run, skip.
            var alreadyInPlace = string.Equals(
                Path.GetFullPath(sourceDir).TrimEnd('\\'),
                Path.GetFullPath(installDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            // Sanity check the payload before we touch the running service.
            // The single most common foot-gun is `dotnet publish` running
            // without a fresh qos-web/dist copied to aot/wwwroot/ - the
            // install would succeed but the dashboard would be empty.
            var sourceWwwroot = Path.Combine(sourceDir, "wwwroot");
            if (!Directory.Exists(sourceWwwroot) ||
                !File.Exists(Path.Combine(sourceWwwroot, "index.html")))
            {
                Console.Error.WriteLine(
                    $"[install] FATAL: {sourceWwwroot}\\index.html is missing. " +
                    "Did you forget to `npm run build:service` in qos-web and copy dist/ into wwwroot/? " +
                    "Refusing to install without a populated web bundle.");
                return 4;
            }

            if (!alreadyInPlace)
            {
                Log("copying payload to install dir");
                Directory.CreateDirectory(installDir);
                StopAndDeleteServiceIfPresent();
                CopyDirectory(sourceDir, installDir);
            }
            else
            {
                Log("running from install dir, skipping copy");
                StopAndDeleteServiceIfPresent();
            }

            var installedExe = Path.Combine(installDir, BinaryName);
            if (!File.Exists(installedExe))
            {
                Console.Error.WriteLine($"[install] expected EXE not found at {installedExe}");
                return 2;
            }

            // 2. Create and configure the service.
            //
            // sc.exe is finicky: each `key= value` pair must be two separate
            // tokens, with the trailing-space form of the key. binPath value
            // needs internal quotes around the EXE path so the space in
            // "Program Files" doesn't split the launcher arg.
            Log("creating Windows Service");
            if (!RunSc("create", ServiceName,
                "binPath=", $"\"{installedExe}\" --service",
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", ServiceDisplayName,
                "depend=", "PawnIO"))
            {
                return 3;
            }

            RunSc("description", ServiceName, ServiceDescription);
            RunSc("failure", ServiceName,
                "reset=", "86400",
                "actions=", "restart/5000/restart/5000/restart/5000");

            // 3. Grant SERVICE_START to Authenticated Users (no UAC needed for
            // failsafe path - manual stop + double-click recovers without prompt).
            // Stop stays admin-only so malware can't disable.
            try
            {
                ExtendServiceDaclWithAuthUsersStart();
            }
            catch (Exception ex)
            {
                // Non-fatal: install still completes; recovery will prompt UAC.
                Log($"WARN could not extend service DACL: {ex.Message}");
            }

            // 4. Install PawnIO kernel driver (no-op if already registered).
            Log("ensuring PawnIO driver is installed");
            var pawnTask = PawnIoInstaller.EnsureInstalledAsync();
            pawnTask.GetAwaiter().GetResult();

            // 5. Firewall rule for LAN access (phone pairing on private network).
            // Rule name has no spaces and the program path has no embedded
            // quotes - netsh is even pickier than sc.exe about ArgumentList
            // tokenization.
            Log("configuring firewall rule");
            RunNetsh("advfirewall", "firewall", "delete", "rule",
                $"name={FirewallRuleName}");
            RunNetsh("advfirewall", "firewall", "add", "rule",
                $"name={FirewallRuleName}",
                "dir=in", "action=allow",
                $"program={installedExe}",
                "protocol=TCP",
                $"localport={DefaultPort}",
                "profile=private,domain");

            // 6. Add/Remove Programs registration is owned by Inno Setup
            // (the {AppId}_is1 key). We used to write our own HKLM\...\Qos
            // entry here, which caused two rows in the Apps & Features list.
            // Inno's entry is authoritative because its uninstall flow
            // (unins000.exe) also removes the install dir on top of our
            // --uninstall step. We still try to delete the legacy "Qos" key
            // below in case an older install left one behind.
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false); }
            catch (Exception ex) { Log($"WARN legacy uninstall reg cleanup failed: {ex.Message}"); }

            // 7. Start Menu shortcut (best-effort).
            try { CreateStartMenuShortcut(installedExe); }
            catch (Exception ex) { Log($"WARN Start Menu shortcut failed: {ex.Message}"); }

            // 7a. Enable tray autostart by default. Writes HKCU\Run\Qos for
            // the user running the installer. When invoked via Inno (elevated
            // user account), HKCU resolves to the real user. When invoked via
            // a SYSTEM-context test fixture, it writes to SYSTEM's profile
            // which is harmless. The user can opt out later via the tray menu
            // "Start at logon" toggle (also HKCU-writable, no admin needed).
            try
            {
                var startup = new WindowsStartupProvider();
                startup.SetEnabled(true, installedExe, arguments: string.Empty);
                Log("tray autostart enabled (HKCU\\Run\\Qos)");
            }
            catch (Exception ex) { Log($"WARN tray autostart write failed: {ex.Message}"); }

            // 8. Start the service.
            Log("starting QosService");
            RunSc("start", ServiceName);

            // 9. Wait for /ping to confirm.
            if (WaitForPing(TimeSpan.FromSeconds(20)))
            {
                Log("service is responding on /ping");
            }
            else
            {
                Log("WARN service did not respond to /ping within timeout");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[install] failed: {ex}");
            return 99;
        }
    }

    /// <summary>
    /// `Qos.exe --uninstall` entry. Stops + deletes the service, removes the
    /// firewall rule, Add/Remove entry, Start Menu shortcut, and the install
    /// dir (best-effort; locked files scheduled for delete-on-reboot). Does
    /// NOT delete %ProgramData%\Qos\ by default - pass --purge to wipe user
    /// data. PawnIO is left installed (harmless and shared with other tools).
    /// </summary>
    public static int RunUninstall(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        if (!Platform.ProcessElevation.GetCurrent().IsElevated)
        {
            return SelfElevateAndReinvoke("--uninstall");
        }
        var purge = Array.Exists(args, a => string.Equals(a, "--purge", StringComparison.OrdinalIgnoreCase));

        Log("stopping QosService");
        RunSc("stop", ServiceName);
        WaitForServiceStop(TimeSpan.FromSeconds(10));

        // Kill sibling Qos / sidecar processes so sc delete can complete
        // synchronously instead of being deferred until handles release.
        // Without this the service "comes back" on the next boot.
        Log("killing tray / sidecar processes");
        KillSiblingProcesses("Qos.exe");
        KillSiblingProcesses("OpenRGB.exe");
        KillSiblingProcesses("qos-overlay.exe");

        Log("deleting QosService");
        RunSc("delete", ServiceName);

        // Clear the per-user tray autostart. Same HKCU-vs-elevated-token
        // caveat as the install path - acceptable for the consent UAC flow.
        Log("removing tray autostart");
        try { new WindowsStartupProvider().SetEnabled(false, string.Empty, string.Empty); }
        catch (Exception ex) { Log($"WARN HKCU\\Run\\Qos delete failed: {ex.Message}"); }

        Log("removing firewall rule");
        RunNetsh("advfirewall", "firewall", "delete", "rule",
            $"name=\"{FirewallRuleName}\"");

        Log("removing Add/Remove Programs entry");
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log($"WARN reg delete failed: {ex.Message}"); }

        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            InstallDirName);

        try { DeleteShortcut(installDir); } catch { /* best-effort */ }

        Log("removing install dir");
        TryDeleteOrScheduleOnReboot(installDir);

        if (purge)
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                InstallDirName);
            Log($"--purge: deleting {dataDir}");
            TryDeleteOrScheduleOnReboot(dataDir);
        }

        Log("uninstall complete");
        return 0;
    }

    /// <summary>
    /// `Qos.exe --start-service` entry. Unprivileged: works because --install
    /// granted SERVICE_START to Authenticated Users. Useful for scripting and
    /// as an explicit recovery hook the dashboard can shell out to.
    /// </summary>
    public static int RunStartService()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        if (!RunSc("start", ServiceName)) return 2;
        return WaitForPing(TimeSpan.FromSeconds(15)) ? 0 : 3;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int SelfElevateAndReinvoke(string mode)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("[install] cannot determine own EXE path for self-elevation");
            return 100;
        }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = mode,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            var p = Process.Start(psi);
            if (p is null) return 101;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC.
            return 1223;
        }
    }

    private static void StopAndDeleteServiceIfPresent()
    {
        if (RunScSilent("query", ServiceName))
        {
            RunSc("stop", ServiceName);
            WaitForServiceStop(TimeSpan.FromSeconds(8));
            RunSc("delete", ServiceName);
        }
    }

    private static void ExtendServiceDaclWithAuthUsersStart()
    {
        // Read current SDDL via `sc sdshow QosService`, append an ACE granting
        // Authenticated Users SERVICE_QUERY_STATUS + SERVICE_START, write back
        // with `sc sdset`. Inheriting the platform default keeps any future
        // ACEs Windows adds in newer releases.
        var sdshow = RunScCaptureOutput("sdshow", ServiceName);
        if (string.IsNullOrWhiteSpace(sdshow))
        {
            Log("WARN sdshow returned empty; skipping DACL extension");
            return;
        }
        // sdshow output is: blank line, SDDL, blank line.
        var sddl = sdshow.Trim();
        // The ACE we want: allow Authenticated Users (AU) SERVICE_QUERY_STATUS (LC) + SERVICE_START (RP)
        const string newAce = "(A;;LCRP;;;AU)";
        if (sddl.Contains(newAce, StringComparison.Ordinal))
        {
            Log("DACL already grants SERVICE_START to Authenticated Users");
            return;
        }
        // Insert before the SACL part (S:...) if present, otherwise at end.
        var sIdx = sddl.IndexOf("S:", StringComparison.Ordinal);
        string newSddl;
        if (sIdx >= 0)
        {
            newSddl = sddl.Insert(sIdx, newAce);
        }
        else
        {
            newSddl = sddl + newAce;
        }
        if (!RunSc("sdset", ServiceName, newSddl))
        {
            Log("WARN sdset failed");
        }
        else
        {
            Log("granted SERVICE_START to Authenticated Users");
        }
    }

    private static void CreateStartMenuShortcut(string targetExe)
    {
        // Drop a .url shortcut to the dashboard (more useful than the exe itself
        // for end users). We avoid the COM ShellLink approach for AOT safety.
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var dir = Path.Combine(startMenu, "Programs", "Qos");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "Qos Dashboard.url");
        var content = $"[InternetShortcut]\r\nURL=http://localhost:{DefaultPort}/\r\nIconFile={targetExe}\r\nIconIndex=0\r\n";
        File.WriteAllText(lnk, content);
    }

    private static void DeleteShortcut(string installDir)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var dir = Path.Combine(startMenu, "Programs", "Qos");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            // Skip our own .git, obj, bin if present.
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            File.Copy(file, file.Replace(source, target), overwrite: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private static void TryDeleteOrScheduleOnReboot(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Files in use - schedule for delete on reboot.
            foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            Log($"locked files in {path} scheduled for delete on reboot");
        }
        catch (UnauthorizedAccessException)
        {
            // The uninstaller usually IS the Qos.exe being deleted, so the
            // .exe holds its own write lock. Schedule everything for delete
            // on reboot - the next start will reclaim a clean dir.
            foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            Log($"{path} pending delete on reboot (uninstaller holds its own EXE lock)");
        }
    }

    private static bool WaitForPing(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = http.GetAsync($"http://localhost:{DefaultPort}/ping").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(500);
        }
        return false;
    }

    private static void WaitForServiceStop(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = RunScCaptureOutput("query", ServiceName);
            if (string.IsNullOrEmpty(status)) return; // already deleted
            if (status.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(500);
        }
    }

    private static bool RunSc(params string[] args)
    {
        var (code, _) = RunCli("sc.exe", args);
        return code == 0;
    }

    private static bool RunScSilent(params string[] args)
    {
        var (code, _) = RunCli("sc.exe", args, suppressOutput: true);
        return code == 0;
    }

    private static string RunScCaptureOutput(params string[] args)
    {
        var (_, output) = RunCli("sc.exe", args, suppressOutput: true);
        return output;
    }

    private static bool RunNetsh(params string[] args)
    {
        var (code, _) = RunCli("netsh.exe", args);
        return code == 0;
    }

    private static bool KillSiblingProcesses(string imageName)
    {
        // Kill all running processes that match imageName, except the
        // current process. Done in-process rather than via taskkill /T to
        // avoid the documented quirk that /T also descends into the
        // matched process's tree (which could include us transitively).
        var self = Process.GetCurrentProcess().Id;
        var nameNoExt = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;
        var killedAny = false;
        try
        {
            foreach (var p in Process.GetProcessesByName(nameNoExt))
            {
                try
                {
                    if (p.Id == self) continue;
                    p.Kill(entireProcessTree: true);
                    killedAny = true;
                }
                catch { /* race: process exited, or access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* best-effort */ }
        return killedAny;
    }

    private static (int Code, string Output) RunCli(string exe, string[] args, bool suppressOutput = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Pass each arg as a single token; sc.exe is picky about its tokenization.
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (!suppressOutput)
        {
            Log($"{Path.GetFileNameWithoutExtension(exe)} {string.Join(' ', args)} -> {p.ExitCode}");
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());
        }
        return (p.ExitCode, stdout + stderr);
    }

    private static void Log(string msg) => Console.WriteLine($"[install] {msg}");
}
#endif
