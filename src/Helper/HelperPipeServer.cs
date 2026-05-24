#if WINDOWS
using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Helper;

/// <summary>
/// Named-pipe accept loop. One server instance at a time; on
/// WaitForConnectionAsync it hands the pipe off to HelperConnection.RunAsync
/// and immediately re-arms by creating the next server instance. The pipe
/// is created with a DACL granting access only to LocalSystem and the
/// interactive user; that replaces any bearer-token handshake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperPipeServer : BackgroundService
{
    public const string PipeName = "Nexus.Helper";

    private readonly HelperRegistry _registry;

    public HelperPipeServer(HelperRegistry registry)
    {
        _registry = registry;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sec = BuildSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName: PipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous,
                    inBufferSize: 64 * 1024,
                    outBufferSize: 64 * 1024,
                    pipeSecurity: sec);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[helper-pipe] create failed: {ex.Message}");
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { server.Dispose(); } catch { }
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[helper-pipe] wait failed: {ex.Message}");
                try { server.Dispose(); } catch { }
                continue;
            }

            // Hand off; the next iteration immediately accepts the next helper.
            // RunAsync owns the server pipe lifetime from here.
            _ = HelperConnection.RunAsync(server, _registry, ct);
        }
    }

    private static PipeSecurity BuildSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        // INTERACTIVE (S-1-5-4): users currently logged on at the console or
        // RDP-attached. Helper process is launched into this group via the
        // schtasks /RU <username> /IT hop, so it carries this SID.
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return sec;
    }
}
#endif
