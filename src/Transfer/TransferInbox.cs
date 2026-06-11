using System.IO;
using Nexus.Service.Models.Transfer;
using Nexus.Service.Persistence;

namespace Nexus.Service.Transfer;

/// <summary>
/// Native-notification request for a transfer that landed while no dashboard
/// was open to show the WebSocket toast. <see cref="FolderPath"/> set means
/// "clicking should open this folder".
/// </summary>
public sealed record TransferAttentionNotice(string Title, string Text, string? FolderPath);

/// <summary>
/// Destination folder + safe-write helper for phone→PC transfers. Resolution
/// order: explicit settings override → the interactive user's Downloads/Nexus
/// (helper-reported on Windows — the Session-0 service can't resolve per-user
/// known folders itself) → CommonApplicationData/Nexus/inbox.
/// </summary>
public sealed class TransferInbox
{
    /// <summary>Per-request cap for /transfer/items — phone videos routinely exceed the global 100 MB Kestrel limit.</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxClipboardChars = 1024 * 1024;

    private const string PartialSuffix = ".nexus-partial";

    // Win32 rejects or remaps these names even when an extension follows.
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    private readonly IConfigStore _store;
    private readonly IServiceProvider _services;

    public TransferInbox(IConfigStore store, IServiceProvider services)
    {
        _store = store;
        _services = services;
    }

    /// <summary>
    /// Raised when a transfer lands with no dashboard subscribed to the
    /// "transfer" topic. Platform bootstraps subscribe to surface a native
    /// notification (Windows tray balloon today).
    /// </summary>
    public event Action<TransferAttentionNotice>? TransferNeedsAttention;

    public void RaiseAttention(TransferAttentionNotice notice)
    {
        // Raised synchronously inside the HTTP handler; a throwing subscriber
        // must not turn an already-saved upload into a 500 (→ phone retry →
        // duplicate files).
        try
        {
            TransferNeedsAttention?.Invoke(notice);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[transfer-notify] subscriber failed: {ex.Message}");
        }
    }

    public string ResolveDir()
    {
        var configured = _store.Load().TransferInboxPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

#if WINDOWS
        var downloads = _services.GetService<Nexus.Service.Helper.HelperRegistry>()?.GetAny()?.DownloadsDir;
        if (!string.IsNullOrWhiteSpace(downloads))
            return Path.Combine(downloads, "Nexus");
#else
        var userDownloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(userDownloads))
            return Path.Combine(userDownloads, "Nexus");
#endif
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "inbox");
    }

    public async Task<TransferSavedItem> SaveAsync(Stream content, string? rawFileName, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var name = SanitizeFileName(rawFileName);
        // Stage in the destination dir so the final rename is same-volume atomic;
        // readers never observe a partial file under its real name. The random
        // suffix keeps concurrent same-name uploads off a shared staging path.
        var partial = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}{PartialSuffix}");
        try
        {
            long size;
            await using (var stream = File.Create(partial))
            {
                await content.CopyToAsync(stream, ct);
                size = stream.Length;
            }
            for (var attempt = 0; ; attempt++)
            {
                var candidate = UniqueName(dir, name);
                var dest = Path.Combine(dir, candidate);
                try
                {
                    // Claim the name with O_EXCL first: File.Move's no-overwrite
                    // check is not atomic on Unix, so concurrent same-name saves
                    // silently replace each other. The placeholder is then swapped
                    // for the real content atomically.
                    using (new FileStream(dest, FileMode.CreateNew, FileAccess.Write)) { }
                }
                // Name taken concurrently — take the next free one.
                catch (IOException) when (attempt < 50)
                {
                    continue;
                }
                try
                {
                    File.Move(partial, dest, overwrite: true);
                }
                catch
                {
                    try { File.Delete(dest); } catch { }
                    throw;
                }
                return new TransferSavedItem { Name = candidate, Size = size };
            }
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>Best-effort removal of staging files orphaned by a crash mid-upload.</summary>
    public static void SweepStalePartials(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            foreach (var stale in Directory.EnumerateFiles(dir, "*" + PartialSuffix))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff)
                        File.Delete(stale);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>False only when the inbox volume is confidently too small for the incoming bytes (512 MB headroom); unknown volumes pass.</summary>
    public static bool HasFreeSpace(string dir, long incomingBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root))
                return true;
            return new DriveInfo(root).AvailableFreeSpace > incomingBytes + 512L * 1024 * 1024;
        }
        catch
        {
            return true;
        }
    }

    internal static string SanitizeFileName(string? raw)
    {
        // GetFileName strips directory components — and with them any ../ traversal.
        // '\' is normalized first: sender-supplied names may use Windows separators
        // even when the service runs on a platform where '\' is a legal name char.
        var name = Path.GetFileName((raw ?? "").Trim().Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x20 || Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        name = new string(chars).TrimStart('.').TrimEnd(' ', '.');
        if (name.Length == 0)
            return "transfer";
        var ext = Path.GetExtension(name);
        // Sender-controlled; an unbounded "extension" would defeat the stem cap.
        // Win32 strips trailing spaces, so trim after capping or the reported
        // name can differ from the on-disk one.
        if (ext.Length > 24)
            ext = ext[..24].TrimEnd(' ');
        var stem = Path.GetFileNameWithoutExtension(name);
        // Win32 reserves the name up to the FIRST dot: CON.foo.txt is still CON.
        if (WindowsReservedNames.Contains(name.Split('.', 2)[0].TrimEnd(' ')))
            stem = "_" + stem;
        if (stem.Length > 120)
            stem = stem[..120];
        return stem + ext;
    }

    private static string UniqueName(string dir, string name)
    {
        if (!File.Exists(Path.Combine(dir, name)))
            return name;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate)))
                return candidate;
        }
    }
}
