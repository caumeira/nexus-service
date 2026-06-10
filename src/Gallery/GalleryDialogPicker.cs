using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Platform;
#if LINUX
using Nexus.Service.Platform.Linux;
#endif

namespace Nexus.Service.Gallery;

public interface IGalleryDialogPicker
{
    Task<GalleryPickResponse> PickAsync(bool folder, CancellationToken ct);
}

/// <summary>
/// Opens the OS-native file/folder picker on the host PC and returns the
/// chosen absolute paths. The dialog always appears on the host machine —
/// browsers never expose real filesystem paths, so a remote dashboard
/// triggering this sees the dialog open on the PC. One dialog at a time.
///
/// Windows: the service is Session-0 LocalSystem and cannot show UI, so the
/// dialog runs in the user-session helper (IFileOpenDialog via the
/// dialog.pick helper command). macOS: the service runs as the user inside
/// Nexus.app, so osascript's `choose file`/`choose folder` works directly.
/// Linux: the root daemon spawns zenity/kdialog inside the user's session
/// via the setpriv wrapper (same trick as the screencast helper).
/// </summary>
public sealed class GalleryDialogPicker : IGalleryDialogPicker
{
    private const int DialogTimeoutMs = 600_000;

    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GalleryDialogPicker(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<GalleryPickResponse> PickAsync(bool folder, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return Fail("a file dialog is already open on the PC");
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return await PickWindowsAsync(folder, ct);
            }

            if (OperatingSystem.IsMacOS())
            {
                return await PickMacAsync(folder, ct);
            }

            if (OperatingSystem.IsLinux())
            {
                return await PickLinuxAsync(folder, ct);
            }

            return Fail("native dialogs are not supported on this platform");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GalleryPickResponse> PickWindowsAsync(bool folder, CancellationToken ct)
    {
#if WINDOWS
        var registry = _services.GetService<Helper.HelperRegistry>();
        if (registry is null)
        {
            return Fail("user-session helper unavailable");
        }

        var result = await Helper.Domains.FileDialogCommands.PickAsync(registry, folder, ct).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return Fail(result.Error);
        }

        return result.Cancelled
            ? new GalleryPickResponse { Cancelled = true }
            : new GalleryPickResponse { Paths = result.Paths };
#else
        await Task.CompletedTask;
        return Fail("not built for windows");
#endif
    }

    private static async Task<GalleryPickResponse> PickMacAsync(bool folder, CancellationToken ct)
    {
        // osascript exits non-zero on user cancel ("User canceled. (-128)");
        // ShellExecutor surfaces that as empty stdout, which maps to Cancelled.
        // "tell me to activate" fronts the chooser — without it the dialog can
        // open behind the Nexus window.
        var script = folder
            ? "tell me to activate\n"
              + "return POSIX path of (choose folder with prompt \"Add a folder to the Nexus gallery\")"
            : "tell me to activate\n"
              + "set out to \"\"\n"
              + "repeat with f in (choose file with prompt \"Add images to the Nexus gallery\" of type {\"public.image\"} with multiple selections allowed)\n"
              + "set out to out & POSIX path of f & \"\\n\"\n"
              + "end repeat\n"
              + "return out";
        var stdout = await ShellExecutor.RunAsync("/usr/bin/osascript", DialogTimeoutMs, ct, "-e", script).ConfigureAwait(false);
        return FromLines(stdout);
    }

#if LINUX
    private static async Task<GalleryPickResponse> PickLinuxAsync(bool folder, CancellationToken ct)
    {
        string tool;
        List<string> args;
        if (ToolExists("zenity"))
        {
            tool = "zenity";
            args = folder
                ? new List<string> { "--file-selection", "--directory" }
                : new List<string>
                {
                    "--file-selection", "--multiple", "--separator=\n",
                    "--file-filter=Images | *.jpg *.jpeg *.png *.webp *.gif *.bmp *.avif",
                };
        }
        else if (ToolExists("kdialog"))
        {
            tool = "kdialog";
            args = folder
                ? new List<string> { "--getexistingdirectory", "." }
                : new List<string>
                {
                    "--getopenfilename", ".",
                    "Image files (*.jpg *.jpeg *.png *.webp *.gif *.bmp *.avif)",
                    "--multiple", "--separate-output",
                };
        }
        else
        {
            return Fail("no dialog tool found on this PC (install zenity or kdialog)");
        }

        // Root daemon → run the dialog inside the user's compositor session.
        var (file, wrapped) = LinuxSession.WrapSpawnAsSessionUser(tool, args);
        var stdout = await ShellExecutor.RunAsync(file, DialogTimeoutMs, ct, wrapped.ToArray()).ConfigureAwait(false);
        return FromLines(stdout);
    }

    private static bool ToolExists(string tool) =>
        !string.IsNullOrWhiteSpace(ShellExecutor.Run("which", tool));
#else
    private static Task<GalleryPickResponse> PickLinuxAsync(bool folder, CancellationToken ct) =>
        Task.FromResult(Fail("not built for linux"));
#endif

    private static GalleryPickResponse FromLines(string stdout)
    {
        var paths = stdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && Path.IsPathFullyQualified(l))
            .ToList();
        return paths.Count == 0
            ? new GalleryPickResponse { Cancelled = true }
            : new GalleryPickResponse { Paths = paths };
    }

    private static GalleryPickResponse Fail(string msg) =>
        new() { Error = true, Msg = msg };
}
