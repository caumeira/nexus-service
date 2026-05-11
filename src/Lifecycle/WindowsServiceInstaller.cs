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
/// Canonical install / uninstall primitive for qOS as a Windows Service.
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
    public const string ServiceDisplayName = "qOS Service";
    public const string ServiceDescription = "qOS hardware monitoring and control";
    public const string InstallDirName = "qOS";
    public const string BinaryName = "qOS.exe";
    public const string FirewallRuleName = "qOSService";
    public const string UninstallRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\qOS";
    public const int DefaultPort = 9400;

    /// <summary>
    /// `qOS.exe --install` entry. Self-elevates if needed; copies files to
    /// %ProgramFiles%\qOS\ if invoked from elsewhere; registers the Windows
    /// Service; installs PawnIO; opens the firewall; writes the Add/Remove
    /// Programs registry key; starts the service.
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

            // 6. Add/Remove Programs registration.
            Log("writing uninstall registry key");
            WriteUninstallRegistry(installDir, installedExe);

            // 7. Start Menu shortcut (best-effort).
            try { CreateStartMenuShortcut(installedExe); }
            catch (Exception ex) { Log($"WARN Start Menu shortcut failed: {ex.Message}"); }

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
    /// `qOS.exe --uninstall` entry. Stops + deletes the service, removes the
    /// firewall rule, Add/Remove entry, Start Menu shortcut, and the install
    /// dir (best-effort; locked files scheduled for delete-on-reboot). Does
    /// NOT delete %ProgramData%\qOS\ by default - pass --purge to wipe user
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

        Log("deleting QosService");
        RunSc("delete", ServiceName);

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
    /// `qOS.exe --start-service` entry. Unprivileged: works because --install
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

    private static void WriteUninstallRegistry(string installDir, string installedExe)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallRegKey, writable: true);
        if (key is null)
        {
            Log("WARN could not open uninstall reg key");
            return;
        }
        key.SetValue("DisplayName", "qOS");
        key.SetValue("DisplayVersion", ResolveVersion());
        key.SetValue("Publisher", "Nexus qOS");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{installedExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{installedExe}\" --uninstall --silent");
        key.SetValue("DisplayIcon", installedExe);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string ResolveVersion()
    {
        try { return Qos.Service.BuildInfo.Version; }
        catch { return "1.0.0"; }
    }

    private static void CreateStartMenuShortcut(string targetExe)
    {
        // Drop a .url shortcut to the dashboard (more useful than the exe itself
        // for end users). We avoid the COM ShellLink approach for AOT safety.
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var dir = Path.Combine(startMenu, "Programs", "qOS");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "qOS Dashboard.url");
        var content = $"[InternetShortcut]\r\nURL=http://localhost:{DefaultPort}/\r\nIconFile={targetExe}\r\nIconIndex=0\r\n";
        File.WriteAllText(lnk, content);
    }

    private static void DeleteShortcut(string installDir)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var dir = Path.Combine(startMenu, "Programs", "qOS");
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
            // The uninstaller usually IS the qOS.exe being deleted, so the
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
