using System.Collections.Generic;
using System.Threading.Tasks;
#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Migration.FanControl;

/// <summary>A saved FanControl configuration file.</summary>
public sealed record FanControlConfigFile(string Path, string Name, long ModifiedUnixMs, bool IsDefault);

/// <summary>
/// Detection result for a FanControl install. Detected means present now;
/// Configs outlive an uninstall, which is why the import stays offered after
/// the app is gone.
/// </summary>
public sealed record FanControlDetectionResult(
    bool Detected,
    bool Running,
    string? InstallLocation,
    string? Version,
    IReadOnlyList<FanControlConfigFile> Configs,
    bool AutostartPresent)
{
    public static readonly FanControlDetectionResult None =
        new(false, false, null, null, new List<FanControlConfigFile>(), false);

    public bool ImportAvailable => Configs.Count > 0;
}

/// <summary>
/// Finds a FanControl install and its saved configurations. FanControl is
/// Windows-only, so every other platform gets the stub. Each probe is
/// read-only and independent: a missing key or a locked file never blocks the
/// others.
/// </summary>
public interface IFanControlDetector
{
    FanControlDetectionResult Detect();

    /// <summary>Reads a config file, but only one this detector actually listed, so a caller cannot turn this into an arbitrary file read.</summary>
    string? ReadConfig(string path);

    /// <summary>Closes a running FanControl. Graceful first: it owns fan control on this machine, and killing it leaves fans wherever they were.</summary>
    Task<bool> CloseAppAsync();

    /// <summary>Removes FanControl's start-with-Windows entry. True when it is gone or was never there.</summary>
    bool DisableAutostart();
}

#if WINDOWS
public sealed class FanControlDetector : IFanControlDetector
{
    private const string ProcessName = "FanControl";
    private const string TaskName = "FanControl";
    private const string ConfigDirName = "Configurations";
    private const string DefaultConfigName = "userConfig.json";
    private const string HklmUninstall64Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string HklmUninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public FanControlDetectionResult Detect()
    {
        var (installLocation, version) = ReadUninstallEntry();
        var running = IsRunning();
        var runningDir = running ? RunningExeDirectory() : null;
        installLocation ??= runningDir;

        var root = FindConfigRoot(installLocation, runningDir);
        var configs = root is null ? new List<FanControlConfigFile>() : ListConfigs(root);
        var detected = installLocation is not null && SafeDirExists(installLocation);

        return new FanControlDetectionResult(
            detected || running, running, installLocation, version, configs, AutostartPresent());
    }

    public string? ReadConfig(string path)
    {
        // Only a path this detector itself listed is readable: the route takes
        // the path from the client, and the service runs as LocalSystem.
        var listed = Detect().Configs;
        var match = listed.FirstOrDefault(c =>
            string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }
        try
        {
            return File.ReadAllText(match.Path);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CloseAppAsync()
    {
        Process[] running;
        try
        {
            running = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            return false;
        }

        if (running.Length == 0)
        {
            return true;
        }

        foreach (var p in running)
        {
            try { p.CloseMainWindow(); } catch { /* per-process swallow */ }
        }

        // FanControl restores fans to BIOS on a clean exit; give it that chance
        // before forcing, or the fans stay wherever it last drove them.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (!IsRunning())
            {
                return true;
            }
        }

        foreach (var p in running)
        {
            try { if (!p.HasExited) p.Kill(); } catch { /* per-process swallow */ }
        }
        await Task.Delay(500).ConfigureAwait(false);
        return !IsRunning();
    }

    public bool DisableAutostart()
    {
        var ok = true;

        if (ScheduledTaskFileExists())
        {
            ok &= Nexus.Service.Platform.ShellExecutor.RunExit(
                "schtasks.exe", 10000, "/Delete", "/TN", TaskName, "/F") == 0;
        }

        try
        {
            var sid = ConsoleUserSid.Resolve(RunKeyPath);
            if (sid is not null)
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\{RunKeyPath}", writable: true);
                if (key?.GetValue(ProcessName) is not null)
                {
                    key.DeleteValue(ProcessName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            ok = false;
        }

        return ok;
    }

    private static bool AutostartPresent()
    {
        if (ScheduledTaskFileExists())
        {
            return true;
        }
        try
        {
            var sid = ConsoleUserSid.Resolve(RunKeyPath);
            if (sid is null)
            {
                return false;
            }
            using var key = Registry.Users.OpenSubKey($@"{sid}\{RunKeyPath}");
            return key?.GetValue(ProcessName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ScheduledTaskFileExists()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", TaskName));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName(ProcessName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunningExeDirectory()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                // MainModule throws for a process in another session; the
                // registry and the well-known paths cover that case.
                try
                {
                    var file = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file))
                    {
                        return Path.GetDirectoryName(file);
                    }
                }
                catch { /* per-process swallow */ }
            }
        }
        catch { /* swallow */ }
        return null;
    }

    /// <summary>Uninstall entry (both registry views) whose DisplayName is FanControl.</summary>
    private static (string? InstallLocation, string? Version) ReadUninstallEntry()
    {
        foreach (var path in new[] { HklmUninstall64Path, HklmUninstall32Path })
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root is null)
                {
                    continue;
                }
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display)
                    {
                        continue;
                    }
                    if (!display.Equals("FanControl", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var location = entry.GetValue("InstallLocation") as string;
                    var version = entry.GetValue("DisplayVersion") as string;
                    return (string.IsNullOrWhiteSpace(location) ? null : location.TrimEnd('\\'), version);
                }
            }
            catch { /* per-view swallow */ }
        }
        return (null, null);
    }

    /// <summary>
    /// The folder holding Configurations\: the install dir when known, then the
    /// running exe's dir, then the usual install targets including portable
    /// unpacks under each user profile.
    /// </summary>
    private static string? FindConfigRoot(string? installLocation, string? runningDir)
    {
        foreach (var candidate in CandidateRoots(installLocation, runningDir))
        {
            if (SafeDirExists(Path.Combine(candidate, ConfigDirName)))
            {
                return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots(string? installLocation, string? runningDir)
    {
        if (!string.IsNullOrEmpty(installLocation)) yield return installLocation;
        if (!string.IsNullOrEmpty(runningDir)) yield return runningDir;

        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "FanControl");
            }
        }

        // Portable unpacks live under a user profile, which the LocalSystem
        // service has to reach by path rather than through its own environment.
        foreach (var profile in Nexus2ProfileDirs.Candidates())
        {
            yield return Path.Combine(profile, "Downloads", "FanControl");
            yield return Path.Combine(profile, "Documents", "FanControl");
            yield return Path.Combine(profile, "AppData", "Local", "FanControl");
            yield return Path.Combine(profile, "AppData", "Local", "Programs", "FanControl");
            yield return Path.Combine(profile, "AppData", "Roaming", "FanControl");
        }
    }

    private static List<FanControlConfigFile> ListConfigs(string root)
    {
        var configs = new List<FanControlConfigFile>();
        try
        {
            var dir = Path.Combine(root, ConfigDirName);
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0)
                    {
                        continue;
                    }
                    var name = Path.GetFileNameWithoutExtension(file);
                    configs.Add(new FanControlConfigFile(
                        file,
                        name,
                        new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        string.Equals(info.Name, DefaultConfigName, StringComparison.OrdinalIgnoreCase)));
                }
                catch { /* per-file swallow */ }
            }
        }
        catch { /* swallow */ }

        // Default first, then most recently saved.
        configs.Sort((a, b) => a.IsDefault != b.IsDefault
            ? (a.IsDefault ? -1 : 1)
            : b.ModifiedUnixMs.CompareTo(a.ModifiedUnixMs));
        return configs;
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
#else
public sealed class FanControlDetector : IFanControlDetector
{
    public FanControlDetectionResult Detect() => FanControlDetectionResult.None;

    public string? ReadConfig(string path) => null;

    public Task<bool> CloseAppAsync() => Task.FromResult(false);

    public bool DisableAutostart() => false;
}
#endif
