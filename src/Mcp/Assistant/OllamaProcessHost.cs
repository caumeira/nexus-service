using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Mcp.Assistant;

/// <summary>Spawns and supervises the <c>ollama serve</c> child process. A seam so
/// <see cref="OllamaRuntimeManager"/> tests can fake process lifecycle without
/// spawning a real child.</summary>
public interface IOllamaProcessHost
{
    IOllamaProcessHandle Start(string exePath, string workingDirectory, IReadOnlyDictionary<string, string> environment);
}

public interface IOllamaProcessHandle
{
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken ct);
    void Kill();
}

/// <summary>Real child process, launched with an argument array (never a shell
/// string) and its working directory pinned to the binary's own directory so
/// sibling-DLL loads resolve (same lesson as the bundled adb transport).</summary>
public sealed class SystemOllamaProcessHost : IOllamaProcessHost
{
    public IOllamaProcessHandle Start(string exePath, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add("serve");
        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exePath}.");
#if WINDOWS
        // Reap with us on an abrupt exit instead of leaving an orphaned ollama
        // serve holding the port (which risks being mis-adopted as a system
        // install on the next launch).
        Nexus.Service.Lifecycle.ChildProcessJob.Assign(process);
#endif
        return new RealProcessHandle(process);
    }

    private sealed class RealProcessHandle : IOllamaProcessHandle
    {
        private readonly Process _process;
        public RealProcessHandle(Process process) => _process = process;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }
}
