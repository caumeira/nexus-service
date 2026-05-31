using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// KDE/Plasma system tray icon via D-Bus StatusNotifierItem + com.canonical.dbusmenu.
/// Consumes the shared <see cref="DBusConnection"/>. Menu items spawn xdg-open /
/// Firefox Flatpak to open the dashboard.
/// </summary>
public sealed class LinuxTrayHost : IDisposable
{
    private readonly DBusConnection _dbus;
    private readonly string _url;
    private readonly Action _onQuit;
    private readonly int _pid = Environment.ProcessId;

    private const int MenuRoot = 0;
    private const int MenuOpenDashboard = 1;
    private const int MenuSettings = 2;
    private const int MenuSeparator = 3;
    private const int MenuQuit = 4;

    public LinuxTrayHost(DBusConnection dbus, string url, Action onQuit)
    {
        _dbus = dbus;
        _url = url;
        _onQuit = onQuit;
    }

    public async Task StartAsync()
    {
        _dbus.RegisterHandler("/StatusNotifierItem", HandleSni);
        _dbus.RegisterHandler("/MenuBar", HandleMenu);

        var sniName = $"org.kde.StatusNotifierItem-{_pid}-1";
        _ = await _dbus.RequestNameAsync(sniName);

        try
        {
            await _dbus.CallAsync("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s",
                w => w.WriteString(_dbus.UniqueName));
            Console.Error.WriteLine($"[tray] SNI registered at {_dbus.UniqueName}/StatusNotifierItem");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] watcher registration failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _dbus.UnregisterHandler("/StatusNotifierItem");
        _dbus.UnregisterHandler("/MenuBar");
    }

    private DBusMessage? HandleSni(DBusMessage msg)
    {
        var iface = msg.Interface ?? "";
        var member = msg.Member ?? "";

        if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            return _dbus.MakeReply(msg, "s", w => w.WriteString(SniIntrospection));
        }

        if (iface == "org.freedesktop.DBus.Properties")
        {
            return HandleSniProperties(msg, member);
        }

        if (iface == "org.kde.StatusNotifierItem")
        {
            switch (member)
            {
                case "Activate":
                case "SecondaryActivate":
                    OpenUrl(_url);
                    return _dbus.MakeReply(msg, "", null);
                case "Scroll":
                case "ContextMenu":
                case "ProvideXdgActivationToken":
                    return _dbus.MakeReply(msg, "", null);
            }
        }

        return _dbus.MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownMethod",
            $"{iface}.{member}");
    }

    private DBusMessage HandleSniProperties(DBusMessage msg, string member)
    {
        if (member == "GetAll")
        {
            return _dbus.MakeReply(msg, "a{sv}", w =>
            {
                w.OpenArray(alignment: 8);
                WriteSniProperties(w);
                w.CloseArray();
            });
        }
        if (member == "Get")
        {
            var r = new DBusReader(msg.Body);
            _ = r.ReadString();
            var prop = r.ReadString();
            return _dbus.MakeReply(msg, "v", w => WriteSniProperty(w, prop));
        }
        return _dbus.MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownMethod", member);
    }

    private void WriteSniProperties(DBusWriter w)
    {
        WriteDictEntry(w, "Category", "s", ww => ww.WriteString("ApplicationStatus"));
        WriteDictEntry(w, "Id", "s", ww => ww.WriteString("nexus-service"));
        WriteDictEntry(w, "Title", "s", ww => ww.WriteString("Nexus"));
        WriteDictEntry(w, "Status", "s", ww => ww.WriteString("Active"));
        WriteDictEntry(w, "IconName", "s", ww => ww.WriteString("nexus"));
        WriteDictEntry(w, "ItemIsMenu", "b", ww => ww.WriteBool(false));
        WriteDictEntry(w, "Menu", "o", ww => ww.WriteObjectPath("/MenuBar"));
        WriteDictEntry(w, "IconThemePath", "s", ww => ww.WriteString(""));
        WriteDictEntry(w, "AttentionIconName", "s", ww => ww.WriteString(""));
        WriteDictEntry(w, "OverlayIconName", "s", ww => ww.WriteString(""));
    }

    private static void WriteSniProperty(DBusWriter w, string prop)
    {
        switch (prop)
        {
            case "Category":
                w.WriteVariant("s", ww => ww.WriteString("ApplicationStatus"));
                return;
            case "Id":
                w.WriteVariant("s", ww => ww.WriteString("nexus-service"));
                return;
            case "Title":
                w.WriteVariant("s", ww => ww.WriteString("Nexus"));
                return;
            case "Status":
                w.WriteVariant("s", ww => ww.WriteString("Active"));
                return;
            case "IconName":
                w.WriteVariant("s", ww => ww.WriteString("nexus"));
                return;
            case "ItemIsMenu":
                w.WriteVariant("b", ww => ww.WriteBool(false));
                return;
            case "Menu":
                w.WriteVariant("o", ww => ww.WriteObjectPath("/MenuBar"));
                return;
        }
        w.WriteVariant("s", ww => ww.WriteString(""));
    }

    private DBusMessage? HandleMenu(DBusMessage msg)
    {
        var iface = msg.Interface ?? "";
        var member = msg.Member ?? "";

        if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            return _dbus.MakeReply(msg, "s", w => w.WriteString(MenuIntrospection));
        }

        if (iface == "org.freedesktop.DBus.Properties" && member == "GetAll")
        {
            return _dbus.MakeReply(msg, "a{sv}", w =>
            {
                w.OpenArray(alignment: 8);
                WriteDictEntry(w, "Version", "u", ww => ww.WriteUInt32(3u));
                WriteDictEntry(w, "Status", "s", ww => ww.WriteString("normal"));
                WriteDictEntry(w, "TextDirection", "s", ww => ww.WriteString("ltr"));
                w.CloseArray();
            });
        }

        if (iface == "com.canonical.dbusmenu")
        {
            switch (member)
            {
                case "GetLayout":
                    return _dbus.MakeReply(msg, "u(ia{sv}av)", w =>
                    {
                        w.WriteUInt32(1u);
                        w.AlignTo(8);
                        w.WriteInt32(MenuRoot);
                        w.OpenArray(alignment: 8);
                        WriteDictEntry(w, "children-display", "s", ww => ww.WriteString("submenu"));
                        w.CloseArray();
                        w.OpenArray(alignment: 1);
                        WriteMenuItemVariant(w, MenuOpenDashboard, "Open Dashboard", false);
                        WriteMenuItemVariant(w, MenuSettings, "Settings", false);
                        WriteMenuItemVariant(w, MenuSeparator, "", true);
                        WriteMenuItemVariant(w, MenuQuit, "Quit Nexus", false);
                        w.CloseArray();
                    });
                case "GetGroupProperties":
                    return _dbus.MakeReply(msg, "a(ia{sv})", w =>
                    {
                        w.OpenArray(alignment: 8);
                        WriteMenuItemStruct(w, MenuOpenDashboard, "Open Dashboard", false);
                        WriteMenuItemStruct(w, MenuSettings, "Settings", false);
                        WriteMenuItemStruct(w, MenuSeparator, "", true);
                        WriteMenuItemStruct(w, MenuQuit, "Quit Nexus", false);
                        w.CloseArray();
                    });
                case "AboutToShow":
                    return _dbus.MakeReply(msg, "b", w => w.WriteBool(false));
                case "Event":
                    HandleMenuEvent(msg);
                    return _dbus.MakeReply(msg, "", null);
                case "EventGroup":
                    HandleMenuEventGroup(msg);
                    return _dbus.MakeReply(msg, "ai", w => { w.OpenArray(alignment: 4); w.CloseArray(); });
            }
        }

        return _dbus.MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownMethod", $"{iface}.{member}");
    }

    private void HandleMenuEvent(DBusMessage msg)
    {
        try
        {
            var r = new DBusReader(msg.Body);
            var id = r.ReadInt32();
            var eventId = r.ReadString();
            if (eventId == "clicked")
            {
                DispatchMenuAction(id);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[tray] menu event: {ex.Message}"); }
    }

    private void HandleMenuEventGroup(DBusMessage msg)
    {
        try
        {
            var r = new DBusReader(msg.Body);
            var len = (int)r.ReadUInt32();
            var end = r.Position + len;
            while (r.Position < end)
            {
                r.AlignTo(8);
                if (r.Position >= end)
                    break;
                var id = r.ReadInt32();
                var eventId = r.ReadString();
                r.SkipVariant();
                _ = r.ReadUInt32();
                if (eventId == "clicked")
                {
                    DispatchMenuAction(id);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[tray] menu eventgroup: {ex.Message}"); }
    }

    private void DispatchMenuAction(int id)
    {
        switch (id)
        {
            case MenuOpenDashboard:
                OpenUrl(_url);
                break;
            case MenuSettings:
                OpenUrl($"{_url}/#/settings");
                break;
            case MenuQuit:
                _onQuit();
                break;
        }
    }

    private static void WriteMenuItemVariant(DBusWriter w, int id, string label, bool separator)
    {
        w.WriteVariant("(ia{sv}av)", ww =>
        {
            ww.AlignTo(8);
            ww.WriteInt32(id);
            ww.OpenArray(alignment: 8);
            if (separator)
            {
                WriteDictEntry(ww, "type", "s", x => x.WriteString("separator"));
            }
            else
            {
                WriteDictEntry(ww, "label", "s", x => x.WriteString(label));
                WriteDictEntry(ww, "enabled", "b", x => x.WriteBool(true));
                WriteDictEntry(ww, "visible", "b", x => x.WriteBool(true));
            }
            ww.CloseArray();
            ww.OpenArray(alignment: 1);
            ww.CloseArray();
        });
    }

    private static void WriteMenuItemStruct(DBusWriter w, int id, string label, bool separator)
    {
        w.AlignTo(8);
        w.WriteInt32(id);
        w.OpenArray(alignment: 8);
        if (separator)
        {
            WriteDictEntry(w, "type", "s", x => x.WriteString("separator"));
        }
        else
        {
            WriteDictEntry(w, "label", "s", x => x.WriteString(label));
            WriteDictEntry(w, "enabled", "b", x => x.WriteBool(true));
            WriteDictEntry(w, "visible", "b", x => x.WriteBool(true));
        }
        w.CloseArray();
    }

    private static void WriteDictEntry(DBusWriter w, string key, string valueSignature, Action<DBusWriter> writeValue)
    {
        w.AlignTo(8);
        w.WriteString(key);
        w.WriteVariant(valueSignature, writeValue);
    }

    /// <summary>
    /// A browser launcher: the binary, any args before the URL, and whether to
    /// pass the URL as a Chromium <c>--app=&lt;url&gt;</c> flag (a clean chromeless
    /// app window — no tabs/URL bar, just native window controls) versus a normal
    /// positional URL argument.
    /// </summary>
    private readonly record struct Launcher(string Path, string[] PreArgs, bool AppMode);

    // Both flatpak export roots — user (~/.local/share/flatpak) is checked before
    // system (/var/lib/flatpak) since a CLI install without root lands in user.
    private static readonly string UserFlatpakBin = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "flatpak", "exports", "bin");
    private const string SysFlatpakBin = "/var/lib/flatpak/exports/bin";

    private static readonly Launcher[] Launchers =
    {
        // Chromium-family in --app mode FIRST: opens the dashboard as a clean,
        // chromeless window — the closest Linux equivalent to the Windows/macOS
        // embedded panel (no native WebView host exists on Linux yet).
        new(Path.Combine(UserFlatpakBin, "org.chromium.Chromium"), Array.Empty<string>(), true),
        new(SysFlatpakBin + "/org.chromium.Chromium", Array.Empty<string>(), true),
        new(Path.Combine(UserFlatpakBin, "com.google.Chrome"), Array.Empty<string>(), true),
        new(SysFlatpakBin + "/com.google.Chrome", Array.Empty<string>(), true),
        new(Path.Combine(UserFlatpakBin, "com.brave.Browser"), Array.Empty<string>(), true),
        new(SysFlatpakBin + "/com.brave.Browser", Array.Empty<string>(), true),
        new(SysFlatpakBin + "/com.microsoft.Edge", Array.Empty<string>(), true),
        new("/usr/bin/chromium", Array.Empty<string>(), true),
        new("/usr/bin/chromium-browser", Array.Empty<string>(), true),
        new("/usr/bin/google-chrome", Array.Empty<string>(), true),
        new("/usr/bin/brave-browser", Array.Empty<string>(), true),
        // Fallbacks: desktop-portal openers launch the default browser in the
        // user's session context (avoids the flatpak sandbox EPERM), normal window.
        new("/usr/bin/xdg-open", Array.Empty<string>(), false),
        new("/usr/bin/kde-open", Array.Empty<string>(), false),
        new("/usr/bin/gio", new[] { "open" }, false),
        // Last resort: Firefox (no --app mode) in a normal window.
        new(UserFlatpakBin + "/org.mozilla.firefox", new[] { "--new-window" }, false),
        new(SysFlatpakBin + "/org.mozilla.firefox", new[] { "--new-window" }, false),
        new("/usr/bin/firefox", new[] { "--new-window" }, false),
    };

    private static void OpenUrl(string url)
    {
        foreach (var l in Launchers)
        {
            if (!File.Exists(l.Path))
                continue;
            try
            {
                var args = new List<string>(l.PreArgs);
                if (l.AppMode)
                {
                    // Force Wayland in a Wayland session: Chromium otherwise
                    // defaults to X11/XWayland and fails on a pure-Wayland login
                    // (missing/invalid XAUTHORITY -> "Missing X server or $DISPLAY").
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                        args.Add("--ozone-platform=wayland");
                    args.Add($"--app={url}");
                }
                else
                {
                    args.Add(url);
                }
                // A root daemon must launch the browser as the session user —
                // Chromium refuses to run as root. Pass-through as a --user run.
                var (spawnFile, spawnArgs) = LinuxSession.WrapSpawnAsSessionUser(l.Path, args);
                var psi = new ProcessStartInfo
                {
                    FileName = spawnFile,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in spawnArgs)
                    psi.ArgumentList.Add(a);
                if (Process.Start(psi) is not null)
                {
                    Console.Error.WriteLine($"[tray] opened {url} via {l.Path}{(l.AppMode ? " (app mode)" : "")}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tray] {l.Path} failed: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[tray] no browser launcher found for {url}");
    }

    private const string SniIntrospection = @"<!DOCTYPE node PUBLIC ""-//freedesktop//DTD D-BUS Object Introspection 1.0//EN""
 ""http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd"">
<node>
  <interface name=""org.kde.StatusNotifierItem"">
    <method name=""Activate""><arg type=""i""/><arg type=""i""/></method>
    <method name=""SecondaryActivate""><arg type=""i""/><arg type=""i""/></method>
    <method name=""Scroll""><arg type=""i""/><arg type=""s""/></method>
    <method name=""ContextMenu""><arg type=""i""/><arg type=""i""/></method>
  </interface>
  <interface name=""org.freedesktop.DBus.Properties"">
    <method name=""Get""><arg type=""s"" direction=""in""/><arg type=""s"" direction=""in""/><arg type=""v"" direction=""out""/></method>
    <method name=""GetAll""><arg type=""s"" direction=""in""/><arg type=""a{sv}"" direction=""out""/></method>
  </interface>
</node>";

    private const string MenuIntrospection = @"<!DOCTYPE node PUBLIC ""-//freedesktop//DTD D-BUS Object Introspection 1.0//EN""
 ""http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd"">
<node>
  <interface name=""com.canonical.dbusmenu"">
    <method name=""GetLayout""><arg type=""i"" direction=""in""/><arg type=""i"" direction=""in""/><arg type=""as"" direction=""in""/><arg type=""u"" direction=""out""/><arg type=""(ia{sv}av)"" direction=""out""/></method>
    <method name=""Event""><arg type=""i"" direction=""in""/><arg type=""s"" direction=""in""/><arg type=""v"" direction=""in""/><arg type=""u"" direction=""in""/></method>
    <method name=""AboutToShow""><arg type=""i"" direction=""in""/><arg type=""b"" direction=""out""/></method>
  </interface>
</node>";
}
