using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Qos.Service.Lifecycle;

/// <summary>
/// Real Windows autostart via Task Scheduler (schtasks.exe).
/// Creates a logon-triggered task at HIGHEST run level so the service
/// starts automatically when the user logs in. Uses /F (force) to
/// overwrite any existing task on re-enable.
/// </summary>
public sealed class WindowsStartupProvider : IStartupProvider
{
    private const string PrimaryTaskName = "QosService";
    private const string LegacyTaskName = "qOS";

    public bool IsEnabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            return IsTaskEnabled(PrimaryTaskName) || IsTaskEnabled(LegacyTaskName);
        }
        catch { return false; }
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            if (enabled)
            {
                // --no-window keeps the service silent at logon: it boots,
                // serves the dashboard, but the Edge app-mode window is not
                // auto-opened. Manual tray / double-click / protocol launches
                // still open it.
                var combinedArgs = string.IsNullOrWhiteSpace(arguments)
                    ? "--no-window"
                    : $"{arguments} --no-window";
                var command = $"\"{path}\" {combinedArgs}";
                var created = RunSchtasks(
                    "/Create",
                    "/TN", PrimaryTaskName,
                    "/TR", command,
                    "/SC", "ONLOGON",
                    "/RL", "HIGHEST",
                    "/F");
                if (created)
                {
                    DeleteTaskIfExists(LegacyTaskName);
                }
                return created;
            }
            else
            {
                return DeleteTaskIfExists(PrimaryTaskName) & DeleteTaskIfExists(LegacyTaskName);
            }
        }
        catch { return false; }
    }

    private static bool IsTaskEnabled(string taskName)
    {
        var result = RunSchtasksWithOutput("/Query", "/TN", taskName, "/FO", "LIST", "/V");
        if (result.ExitCode != 0)
        {
            return false;
        }

        foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Scheduled Task State:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        }

        // Older Windows builds can omit the state line; if the query succeeds,
        // fall back to existence rather than matching unrelated "Disabled"
        // fields such as Idle Time.
        return true;
    }

    private static bool TaskExists(string taskName) =>
        RunSchtasksWithOutput("/Query", "/TN", taskName).ExitCode == 0;

    private static bool DeleteTaskIfExists(string taskName) =>
        !TaskExists(taskName) || RunSchtasks("/Delete", "/TN", taskName, "/F");

    private static bool RunSchtasks(params string[] args) =>
        RunSchtasksWithOutput(args).ExitCode == 0;

    private static (int ExitCode, string Output) RunSchtasksWithOutput(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(10000))
        {
            try
            { proc.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            return (-1, "");
        }

        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        return (proc.ExitCode, output + error);
    }
}
