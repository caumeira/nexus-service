#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains
{
    public sealed class FileDialogRequest { public bool PickFolder { get; set; } }

    public sealed class FileDialogResult
    {
        public List<string> Paths { get; set; } = new();
        public bool Cancelled { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Service-side facade for the native file/folder picker. The Session-0
    /// LocalSystem service cannot show UI, so IFileOpenDialog runs in the
    /// user-session helper. The timeout is deliberately long — the user may
    /// browse for a while; the caller's CancellationToken (request abort)
    /// stops the wait early, though an already-shown dialog stays open until
    /// the user closes it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class FileDialogCommands
    {
        public const string PickType = "dialog.pick";
        private const int TimeoutMs = 600_000;

        public static async Task<FileDialogResult> PickAsync(HelperRegistry r, bool folder, CancellationToken ct = default)
        {
            var conn = r.GetAny();
            if (conn is null)
            {
                return new FileDialogResult { Error = "no interactive user session" };
            }

            var result = await conn.SendCommandAsync(
                PickType,
                new FileDialogRequest { PickFolder = folder },
                AppJsonContext.Default.FileDialogRequest,
                timeoutMs: TimeoutMs,
                ct: ct).ConfigureAwait(false);

            if (!result.Ok || result.Payload is null)
            {
                return new FileDialogResult { Error = result.Error ?? "dialog failed" };
            }

            try
            {
                return JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.FileDialogResult)
                    ?? new FileDialogResult { Error = "bad payload" };
            }
            catch
            {
                return new FileDialogResult { Error = "bad payload" };
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class FileDialogHandler
    {
        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register(FileDialogCommands.PickType, async (env, _) =>
            {
                var req = env.Payload is null
                    ? new FileDialogRequest()
                    : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.FileDialogRequest) ?? new FileDialogRequest();
                var result = await Platform.Windows.NativeFileDialog.ShowAsync(req.PickFolder).ConfigureAwait(false);
                return new HelperResult
                {
                    Id = env.Id ?? "",
                    Ok = true,
                    Payload = JsonSerializer.SerializeToElement(result, AppJsonContext.Default.FileDialogResult),
                };
            });
        }
    }
}
#endif
