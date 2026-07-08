using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Diagnostics;

/// <summary>Result of a bounded shell-out. ExitCode is -1 on spawn failure or a
/// timeout kill; Stdout/Stderr are each truncated to a bounded length.</summary>
public readonly record struct ShellResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Shared shell-out helper for the diagnostics module's powershell/bcdedit/
/// wevtutil calls. Drains stdout and stderr concurrently via ReadToEndAsync so
/// a child writing to the unread stream can never deadlock the caller against
/// a full pipe buffer, and awaits exit under a timeout (killing on expiry)
/// instead of a blocking WaitForExit. Every diagnostics ShellOut should go
/// through this rather than hand-rolling Process.Start + synchronous reads.
/// </summary>
public static class DiagnosticsShell
{
    private const int MaxOutputChars = 1_000_000;

    public static ShellResult Run(string fileName, int timeoutMs, params string[] args) =>
        RunAsync(fileName, timeoutMs, args).GetAwaiter().GetResult();

    public static async Task<ShellResult> RunAsync(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new ShellResult(-1, "", "");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new ShellResult(-1, "", "");
            }

            var stdout = Bound(await stdoutTask.ConfigureAwait(false));
            var stderr = Bound(await stderrTask.ConfigureAwait(false));
            return new ShellResult(proc.ExitCode, stdout, stderr);
        }
        catch
        {
            return new ShellResult(-1, "", "");
        }
    }

    private static string Bound(string text) => text.Length > MaxOutputChars ? text[..MaxOutputChars] : text;
}
