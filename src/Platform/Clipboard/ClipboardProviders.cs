using System;

namespace Nexus.Service.Platform.Clipboard;

/// <summary>
/// Sets the OS clipboard text. Used by the deck "type text" action, which sets
/// the clipboard then injects a paste keystroke — the only Unicode-reliable
/// cross-platform way to insert arbitrary text. Returns false when the platform
/// clipboard tool is unavailable.
/// </summary>
public interface IClipboardProvider
{
    bool SetText(string text);
}

/// <summary>macOS clipboard via <c>pbcopy</c>.</summary>
public sealed class MacClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            ShellExecutor.RunWithStdin("/usr/bin/pbcopy", text ?? "", 2000);
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-mac] pbcopy failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Linux clipboard via <c>wl-copy</c> (Wayland) then <c>xclip</c> (X11).</summary>
public sealed class LinuxClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var payload = text ?? "";
        // wl-copy reads stdin and forks a server; treat a clean exit as success.
        if (ShellExecutor.RunWithStdin("wl-copy", payload, 2000) is not null && WlCopyAvailable())
            return true;
        try
        {
            ShellExecutor.RunWithStdin("xclip", payload, 2000, "-selection", "clipboard");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-linux] no clipboard tool (wl-copy/xclip) available: {ex.Message}");
            return false;
        }
    }

    private static bool WlCopyAvailable()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

/// <summary>Windows clipboard via PowerShell <c>Set-Clipboard</c> (AOT-safe; no OLE/STA COM).</summary>
public sealed class WindowsClipboardProvider : IClipboardProvider
{
    public bool SetText(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            ShellExecutor.RunWithStdin("powershell", text ?? "", 4000,
                "-NoProfile", "-Command", "$input | Set-Clipboard");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[clipboard-win] Set-Clipboard failed: {ex.Message}");
            return false;
        }
    }
}

public sealed class StubClipboardProvider : IClipboardProvider
{
    public bool SetText(string text) => false;
}
