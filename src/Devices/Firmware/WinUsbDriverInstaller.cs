using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Installs the WinUSB driver bound to the DFU bootloader (VID 3402/PID 0A00)
/// so dfu-util can open the device once it re-enumerates into DFU mode. Runs
/// <c>pnputil /add-driver &lt;inf&gt; /install</c> against the bundled, signed
/// <c>dfu-driver/DFU_Bootloader.inf</c>. Idempotent — re-adding an already
/// staged driver is a no-op. Windows-only; a no-op elsewhere (Linux uses a
/// udev rule, macOS needs nothing — both deferred).
/// </summary>
public sealed class WinUsbDriverInstaller
{
    private bool _installedThisSession;

    public string InfPath => Path.Combine(AppContext.BaseDirectory, "dfu-driver", "DFU_Bootloader.inf");

    /// <summary>
    /// Ensure the DFU WinUSB driver is staged. Best-effort: returns true if the
    /// driver was added or is already present. Cached after first success so the
    /// flash path doesn't shell out to pnputil every time.
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        if (_installedThisSession) return true;
        if (!File.Exists(InfPath))
        {
            Console.Error.WriteLine($"[winusb] driver inf not found at {InfPath}");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/add-driver");
            psi.ArgumentList.Add(InfPath);
            psi.ArgumentList.Add("/install");

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            // pnputil exit codes: 0 = added, 259 (ERROR_NO_MORE_ITEMS) / 3010
            // (reboot) are also fine for our purposes. Treat anything non-fatal
            // as success since a previously-staged driver still binds.
            Console.Error.WriteLine($"[winusb] pnputil exit={proc.ExitCode}: {stdout.Trim()} {stderr.Trim()}");
            _installedThisSession = proc.ExitCode is 0 or 259 or 3010
                || stdout.Contains("already", StringComparison.OrdinalIgnoreCase);
            // Even on a non-zero we may have an older copy already bound; let the
            // flasher proceed and surface a clear dfu-util error if it can't open.
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[winusb] pnputil failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
