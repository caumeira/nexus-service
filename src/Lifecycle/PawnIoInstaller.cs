using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle.Native;

namespace Nexus.Service.Lifecycle;

public enum PawnIoInstallResult
{
    AlreadyInstalled,
    Installed,
    UserDenied,
    Failed,
    NotApplicable, // non-Windows
}

/// <summary>
/// Auto-installs the bundled PawnIO kernel driver via pnputil.
///
/// On first launch, Nexus.exe self-elevates (one UAC prompt) and reruns
/// itself with --install-pawnio. The elevated child runs pnputil /add-driver /install
/// against the bundled PawnIO.inf, which copies the driver to the driver store,
/// creates the root device node, and starts the kernel service. Subsequent
/// launches detect the registered service in the registry and skip the install.
///
/// We use pnputil (not raw SCM) because PawnIO.sys is signed with a regular
/// code-signing cert (not a Microsoft kernel cross-signature). The catalog file
/// (.cat) provides the trust chain that Windows accepts via the Driver Store
/// install path. Direct SCM CreateService → StartService fails with
/// ERROR_INVALID_IMAGE_HASH (577) without going through the catalog.
/// </summary>
public static class PawnIoInstaller
{
    private const string ServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\PawnIO";
    private const string HardwareId = "Root\\PawnIO";

    // SoftwareDevice setup class GUID — matches Class={62f9c741-...} in PawnIO.inf
    private static readonly Guid SoftwareDeviceClassGuid = new("62f9c741-b25a-46ce-b54c-9bccce08b6f2");

    /// <summary>
    /// Ensures the PawnIO kernel driver is installed and running. If the driver
    /// service isn't registered yet, spawns an elevated helper (one-time UAC prompt)
    /// to install it. Subsequent calls return AlreadyInstalled without prompting.
    /// </summary>
    public static async Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return PawnIoInstallResult.NotApplicable;
        }

        if (IsServiceRegistered())
        {
            return PawnIoInstallResult.AlreadyInstalled;
        }

        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            Console.Error.WriteLine($"[pawnio] bundled INF not found at {infPath}");
            return PawnIoInstallResult.Failed;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.Error.WriteLine("[pawnio] cannot determine own exe path");
            return PawnIoInstallResult.Failed;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--install-pawnio",
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked "No" on the UAC prompt
            return PawnIoInstallResult.UserDenied;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pawnio] failed to spawn elevated installer: {ex.Message}");
            return PawnIoInstallResult.Failed;
        }

        if (proc is null)
        {
            return PawnIoInstallResult.Failed;
        }

        try
        { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { return PawnIoInstallResult.Failed; }

        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"[pawnio] elevated installer exited with code {proc.ExitCode}");
            return PawnIoInstallResult.Failed;
        }

        return IsServiceRegistered() ? PawnIoInstallResult.Installed : PawnIoInstallResult.Failed;
    }

    /// <summary>
    /// Elevated entry point. Called when Nexus.exe is launched with the
    /// --install-pawnio command-line arg. Runs pnputil to install the bundled driver.
    /// Returns 0 on success, 1 on failure.
    /// </summary>
    public static int RunElevatedInstall()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return 1;
        }

        try
        {
            return DoInstall();
        }
        catch (Exception ex)
        {
            LogError($"unhandled exception: {ex}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int DoInstall()
    {
        if (!IsCurrentProcessElevated())
        {
            LogError("not running as administrator");
            return 1;
        }

        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            LogError($"bundled INF not found at {infPath}");
            return 1;
        }
        Log($"installing driver from {infPath}");

        // pnputil.exe lives in System32 — use the absolute path so we don't
        // depend on PATH.
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            LogError($"pnputil.exe not found at {pnputil}");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = pnputil,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/add-driver");
        psi.ArgumentList.Add(infPath);
        psi.ArgumentList.Add("/install");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LogError("Process.Start returned null");
                return 1;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Log($"pnputil stdout: {stdout.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Log($"pnputil stderr: {stderr.Trim()}");
            }

            Log($"pnputil exit code: {proc.ExitCode}");

            // pnputil exit codes:
            //   0 = success
            //   259 (ERROR_NO_MORE_ITEMS) = nothing to install
            //   3010 (ERROR_SUCCESS_REBOOT_REQUIRED) = installed but reboot needed
            // We accept 0 and 3010 as success.
            if (proc.ExitCode != 0 && proc.ExitCode != 3010)
            {
                LogError($"pnputil failed with exit code {proc.ExitCode}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            LogError($"pnputil invocation failed: {ex.Message}");
            return 1;
        }

        // pnputil added the driver package to the driver store, but PawnIO is a
        // root-enumerated PnP device — there's no physical device for PnP to
        // auto-enumerate. We have to create the root device node ourselves via
        // SetupAPI, then call UpdateDriverForPlugAndPlayDevices to bind the
        // driver to it.
        if (!CreateRootDeviceAndBindDriver(infPath))
        {
            LogError("failed to create root device");
            return 1;
        }

        // Verify the service is now registered.
        if (!IsServiceRegistered())
        {
            LogError("driver service not registered after install");
            return 1;
        }

        Log("install complete");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool CreateRootDeviceAndBindDriver(string infPath)
    {
        var classGuid = SoftwareDeviceClassGuid;
        var deviceInfoSet = SetupApi.SetupDiCreateDeviceInfoList(in classGuid, IntPtr.Zero);
        if (deviceInfoSet == SetupApi.INVALID_HANDLE_VALUE)
        {
            LogError($"SetupDiCreateDeviceInfoList failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var deviceInfoData = new SetupApi.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>(),
            };

            if (!SetupApi.SetupDiCreateDeviceInfo(
                    deviceInfoSet,
                    "PawnIO",
                    in classGuid,
                    null,
                    IntPtr.Zero,
                    SetupApi.DICD_GENERATE_ID,
                    ref deviceInfoData))
            {
                LogError($"SetupDiCreateDeviceInfo failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // Hardware ID is REG_MULTI_SZ — double-null-terminated UTF-16
            var hwIdBuffer = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupApi.SetupDiSetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref deviceInfoData,
                    SetupApi.SPDRP_HARDWAREID,
                    hwIdBuffer,
                    (uint)hwIdBuffer.Length))
            {
                LogError($"SetupDiSetDeviceRegistryProperty failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!SetupApi.SetupDiCallClassInstaller(
                    SetupApi.DIF_REGISTERDEVICE,
                    deviceInfoSet,
                    ref deviceInfoData))
            {
                LogError($"SetupDiCallClassInstaller(DIF_REGISTERDEVICE) failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            Log("root device registered");

            // Now bind the driver to our newly created device. INF path must be
            // absolute and the catalog file must be in the same directory.
            bool reboot = false;
            if (!SetupApi.UpdateDriverForPlugAndPlayDevices(
                    IntPtr.Zero,
                    HardwareId,
                    infPath,
                    SetupApi.INSTALLFLAG_FORCE,
                    ref reboot))
            {
                var err = Marshal.GetLastWin32Error();
                LogError($"UpdateDriverForPlugAndPlayDevices failed: {err}");

                // Cleanup the device we just created since the driver bind failed.
                SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_REMOVE, deviceInfoSet, ref deviceInfoData);
                return false;
            }

            Log($"driver bound to root device (reboot needed: {reboot})");
            return true;
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static bool IsServiceRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceRegistryKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "pawnio-install.log");

    private static void Log(string msg)
    {
        Console.Error.WriteLine($"[pawnio-install] {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {msg}\n");
        }
        catch { /* logging is best-effort */ }
    }

    private static void LogError(string msg) => Log("ERROR: " + msg);
}
