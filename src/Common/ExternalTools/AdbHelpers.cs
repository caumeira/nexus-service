using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Shared adb parsing + install utilities used by both the background install
/// strategy and the interactive APK flasher.
/// </summary>
internal static class AdbHelpers
{
    // Matches the first "versionCode=<digits>" in dumpsys package output.
    // The low-32 bits of longVersionCode equal versionCode for all practical package sizes.
    internal static readonly Regex VersionCodeRegex =
        new(@"versionCode=(\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));

    // Matches "versionName=<non-whitespace>" in dumpsys package output.
    internal static readonly Regex VersionNameRegex =
        new(@"versionName=(\S+)", RegexOptions.None, TimeSpan.FromSeconds(5));

    internal static int ParseVersionCode(string dumpsys)
    {
        var m = VersionCodeRegex.Match(dumpsys);
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    internal static string? ParseVersionName(string dumpsys)
    {
        var m = VersionNameRegex.Match(dumpsys);
        return m.Success ? m.Groups[1].Value : null;
    }

    // WorkingDirectory must be the adb dir so LoadLibrary("AdbWinApi.dll") finds its sibling DLLs.
    internal static (bool success, string output) RunAdbInstall(string adbPath, string serial, string apkPath)
        => RunAdb(adbPath, $"-s {serial} install -r \"{apkPath}\"",
            s => s.Contains("Success", StringComparison.Ordinal));

    // Uninstall is needed only on INSTALL_FAILED_UPDATE_INCOMPATIBLE (signing-key change).
    internal static (bool success, string output) RunAdbUninstall(string adbPath, string serial, string package)
        => RunAdb(adbPath, $"-s {serial} uninstall {package}",
            s => s.Contains("Success", StringComparison.Ordinal));

    // INSTALL_FAILED_UPDATE_INCOMPATIBLE means the installed cert differs from the APK cert.
    // Recovery: uninstall (data loss accepted) then reinstall. Only for this error code.
    internal static bool IsSignatureMismatch(string output)
        => output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Install with one automatic signature-recovery retry: if the first attempt fails
    /// with INSTALL_FAILED_UPDATE_INCOMPATIBLE, uninstall the old package and retry once.
    /// Returns <c>recoveryTriggered = true</c> when the recovery path ran; the caller
    /// is responsible for surfacing that in status/log.
    /// <paramref name="uninstallRunner"/> defaults to <see cref="RunAdbUninstall"/>.
    /// </summary>
    internal static (bool success, string output, bool recoveryTriggered) InstallWithSignatureRecovery(
        string adbPath,
        string serial,
        string apkPath,
        string package,
        AdbInstallRunner installRunner,
        AdbUninstallRunner? uninstallRunner = null)
    {
        uninstallRunner ??= RunAdbUninstall;
        var recovery = false;
        (bool success, string output) result = (false, "");

        // USB-FFS streamed installs fail transiently on this hardware; retry a
        // non-signature failure a few times before giving up.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = installRunner(adbPath, serial, apkPath);
            if (result.success)
            {
                return (true, result.output, recovery);
            }

            // Cert changed - uninstall the conflicting package once; the next
            // iteration reinstalls clean.
            if (IsSignatureMismatch(result.output) && !recovery)
            {
                var (unOk, unOut) = uninstallRunner(adbPath, serial, package);
                recovery = true;
                if (!unOk)
                {
                    return (false, $"uninstall failed: {unOut}", recovery);
                }
            }
        }

        return (false, result.output, recovery);
    }

    private static (bool success, string output) RunAdb(string adbPath, string arguments, Func<string, bool> isSuccess)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
            {
                return (false, "Process.Start returned null");
            }

            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(true); } catch { }
                return (false, "timed out after 120s");
            }

            var stdout = p.StandardOutput.ReadToEnd().Trim();
            var stderr = p.StandardError.ReadToEnd().Trim();
            var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout} | {stderr}";
            return (isSuccess(stdout), combined);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
