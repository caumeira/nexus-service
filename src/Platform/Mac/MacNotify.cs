using System.Diagnostics;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// osascript-backed desktop notification — the macOS analog of LinuxNotify /
/// the Windows tray balloon. The mac app runs as the logged-in user, so no
/// session hop is needed. osascript notifications carry no click action.
/// </summary>
internal static class MacNotify
{
    internal static void Send(string title, string body)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display notification {AppleScriptString(body)} with title {AppleScriptString(title)}");
            // macOS 13+ gates this behind a per-app notification permission
            // (attributed to osascript) and silently no-ops with exit 0 until
            // the user allows it — undetectable from here.
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[mac-notify] failed: {ex.Message}");
        }
    }

    private static string AppleScriptString(string s)
        => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
