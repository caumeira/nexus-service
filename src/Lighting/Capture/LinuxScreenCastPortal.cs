using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Drives the org.freedesktop.portal.ScreenCast handshake (CreateSession →
/// SelectSources → Start) over the session D-Bus to obtain a PipeWire node a
/// consumer can stream a monitor from. This is the portable Wayland/X11 capture
/// path (KDE, GNOME, wlroots, X11 all implement it). A root daemon reaches the
/// session bus via <see cref="DBusConnection"/>'s euid-drop, so the portal sees
/// the request as the logged-in user and shows the share dialog on their screen.
///
/// The first Start shows a one-time "share your screen" picker; with
/// persist_mode=persistent the reply carries a restore_token we save and pass
/// back next time so subsequent starts are silent.
///
/// Each portal method returns asynchronously: the method reply is an object path,
/// the real result arrives later as a Request.Response signal on that path. We
/// pass our own handle_token so the request path is predictable, register the
/// signal waiter before the call (no race), and await it.
/// </summary>
public sealed class LinuxScreenCastPortal
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastIface = "org.freedesktop.portal.ScreenCast";

    // SelectSources option values.
    private const uint SourceTypeMonitor = 1;   // types bitmask
    private const uint CursorModeEmbedded = 2;  // draw the cursor into the stream
    private const uint PersistModePersistent = 2; // remember the grant across sessions

    private readonly DBusConnection _bus;
    private int _tokenCounter;
    private bool _matchAdded;

    public LinuxScreenCastPortal(DBusConnection bus) => _bus = bus;

    public sealed record Result(uint NodeId, string? RestoreToken);

    /// <summary>
    /// Run the full handshake and return the PipeWire node id to stream from.
    /// Pass a previously saved <paramref name="restoreToken"/> to skip the picker.
    /// Returns null if the user cancelled or the portal failed.
    /// </summary>
    public async Task<Result?> StartAsync(string? restoreToken)
    {
        await EnsureMatchAsync();

        var (createResp, createRes) = await CallRequestAsync(
            "CreateSession", "a{sv}",
            (w, token) => WriteOptions(w,
                ("handle_token", "s", x => x.WriteString(token)),
                ("session_handle_token", "s", x => x.WriteString($"nexus_sess_{token}"))));
        if (createResp != 0 || !TryGetString(createRes, "session_handle", out var session))
        {
            Console.Error.WriteLine($"[screencast] CreateSession failed (response={createResp})");
            return null;
        }

        var (selResp, _) = await CallRequestAsync(
            "SelectSources", "oa{sv}",
            (w, token) =>
            {
                w.WriteObjectPath(session);
                WriteOptions(w, BuildSelectOptions(token, restoreToken));
            });
        if (selResp != 0)
        {
            Console.Error.WriteLine($"[screencast] SelectSources failed (response={selResp})");
            await CloseSessionAsync(session);
            return null;
        }

        var (startResp, startRes) = await CallRequestAsync(
            "Start", "osa{sv}",
            (w, token) =>
            {
                w.WriteObjectPath(session);
                w.WriteString(""); // parent_window
                WriteOptions(w, ("handle_token", "s", x => x.WriteString(token)));
            },
            timeoutMs: 120000); // user may take a while at the picker
        if (startResp != 0)
        {
            Console.Error.WriteLine($"[screencast] Start cancelled/failed (response={startResp})");
            await CloseSessionAsync(session);
            return null;
        }

        if (!TryGetNodeId(startRes, out var nodeId))
        {
            Console.Error.WriteLine("[screencast] Start returned no stream node");
            await CloseSessionAsync(session);
            return null;
        }
        TryGetString(startRes, "restore_token", out var newToken);
        Console.Error.WriteLine($"[screencast] stream ready, pipewire node {nodeId}");
        return new Result(nodeId, string.IsNullOrEmpty(newToken) ? restoreToken : newToken);
    }

    // Tear down a half-open portal session so a failed/timed-out handshake
    // doesn't leak a server-side session object on the bus.
    private async Task CloseSessionAsync(string session)
    {
        try
        {
            await _bus.CallAsync(PortalService, session,
                "org.freedesktop.portal.Session", "Close", "", null);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[screencast] session close failed: {ex.Message}"); }
    }

    private (string, string, Action<DBusWriter>)[] BuildSelectOptions(string token, string? restoreToken)
    {
        var opts = new List<(string, string, Action<DBusWriter>)>
        {
            ("handle_token", "s", x => x.WriteString(token)),
            ("types", "u", x => x.WriteUInt32(SourceTypeMonitor)),
            ("multiple", "b", x => x.WriteBool(false)),
            ("cursor_mode", "u", x => x.WriteUInt32(CursorModeEmbedded)),
            ("persist_mode", "u", x => x.WriteUInt32(PersistModePersistent)),
        };
        if (!string.IsNullOrEmpty(restoreToken))
            opts.Add(("restore_token", "s", x => x.WriteString(restoreToken)));
        return opts.ToArray();
    }

    private async Task EnsureMatchAsync()
    {
        if (_matchAdded)
            return;
        await _bus.AddMatchAsync(
            "type='signal',interface='org.freedesktop.portal.Request',member='Response'");
        _matchAdded = true;
    }

    /// <summary>
    /// Issue a portal method that uses the Request/Response pattern: pick a
    /// handle_token, predict the request object path, register the Response
    /// waiter first, then call. Returns (response_code, results-dict).
    /// </summary>
    private async Task<(uint, Dictionary<string, object?>)> CallRequestAsync(
        string member, string signature, Action<DBusWriter, string> writeBody, int timeoutMs = 30000)
    {
        var token = $"nexus{++_tokenCounter}";
        var senderToken = _bus.UniqueName.TrimStart(':').Replace('.', '_');
        var reqPath = $"/org/freedesktop/portal/desktop/request/{senderToken}/{token}";

        var responseTask = _bus.WaitForSignalAsync(reqPath, "Response", timeoutMs);
        await _bus.CallAsync(PortalService, PortalPath, ScreenCastIface, member, signature,
            w => writeBody(w, token));
        var signal = await responseTask;

        var r = new DBusReader(signal.Body);
        var response = r.ReadUInt32();
        var results = r.ReadStringVariantDict();
        return (response, results);
    }

    internal static bool TryGetString(Dictionary<string, object?> d, string key, out string value)
    {
        if (d.TryGetValue(key, out var v) && v is string s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    // results["streams"] is a(ua{sv}); the reader boxes it as a list of structs,
    // each [ long nodeId, dict props ]. We want the first stream's node id.
    internal static bool TryGetNodeId(Dictionary<string, object?> d, out uint nodeId)
    {
        nodeId = 0;
        if (d.TryGetValue("streams", out var v) && v is List<object?> streams && streams.Count > 0
            && streams[0] is List<object?> first && first.Count > 0 && first[0] is long n && n >= 0)
        {
            nodeId = (uint)n;
            return true;
        }
        return false;
    }

    internal static void WriteOptions(DBusWriter w, params (string Key, string Sig, Action<DBusWriter> Val)[] entries)
    {
        // a{sv}: array (4-aligned length) of dict-entries (each 8-aligned).
        w.OpenArray(alignment: 8);
        foreach (var (key, sig, val) in entries)
        {
            w.AlignTo(8);
            w.WriteString(key);
            w.WriteVariant(sig, val);
        }
        w.CloseArray();
    }
}
