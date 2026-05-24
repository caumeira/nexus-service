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
        WriteDictEntry(w, "IconName", "s", ww => ww.WriteString("applications-system"));
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
                w.WriteVariant("s", ww => ww.WriteString("applications-system"));
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

    private static readonly string[][] BrowserCandidates =
    {
        new[] { "/var/lib/flatpak/exports/bin/org.mozilla.firefox", "--new-window" },
        new[] { "/var/lib/flatpak/exports/bin/com.brave.Browser", "--new-window" },
        new[] { "/var/lib/flatpak/exports/bin/org.chromium.Chromium", "--new-window" },
        new[] { "/var/lib/flatpak/exports/bin/com.google.Chrome", "--new-window" },
        new[] { "/usr/bin/firefox", "--new-window" },
        new[] { "/usr/bin/chromium-browser", "--new-window" },
        new[] { "/usr/bin/google-chrome", "--new-window" },
        new[] { "/usr/bin/kde-open", "" },
        new[] { "/usr/bin/gio", "open" },
        new[] { "/usr/bin/xdg-open", "" },
    };

    private static void OpenUrl(string url)
    {
        foreach (var cmd in BrowserCandidates)
        {
            if (!File.Exists(cmd[0]))
                continue;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd[0],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                for (int i = 1; i < cmd.Length; i++)
                {
                    if (!string.IsNullOrEmpty(cmd[i]))
                        psi.ArgumentList.Add(cmd[i]);
                }
                psi.ArgumentList.Add(url);
                if (Process.Start(psi) is not null)
                {
                    Console.Error.WriteLine($"[tray] opened {url} via {cmd[0]}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tray] {cmd[0]} failed: {ex.Message}");
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
