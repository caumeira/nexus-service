using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform;

/// <summary>
/// Resolves the path to the logged-in user's current desktop wallpaper for the
/// dashboard's "wallpaper" background mode (the web blurs + veils it).
///
/// The service usually runs as LocalSystem (session 0), where its own profile
/// is SYSTEM's and has no real wallpaper. So it resolves the active console
/// session's user token — LocalSystem holds SE_TCB, so WTSQueryUserToken
/// succeeds — and reads that profile's transcoded wallpaper. When the service
/// runs as the interactive user instead, the token query fails and it falls
/// back to this process's own profile (which is the user's).
/// </summary>
public static class WallpaperReader
{
    [SupportedOSPlatform("windows")]
    public static string? GetCurrentWallpaperPath()
    {
        var profile = ResolveActiveUserProfile()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) return null;
        // Windows keeps the rendered (fit/fill/multi-monitor) current wallpaper
        // here as a JPEG — exactly what we want to blur.
        var transcoded = Path.Combine(
            profile, "AppData", "Roaming", "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveActiveUserProfile()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return null;
            if (!WTSQueryUserToken(session, out token) || token == IntPtr.Zero) return null;

            uint size = 0;
            GetUserProfileDirectoryW(token, null, ref size); // query required length
            if (size == 0) return null;
            var buf = new char[size];
            if (!GetUserProfileDirectoryW(token, buf, ref size)) return null;
            var s = new string(buf);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectoryW(IntPtr hToken, [Out] char[]? lpProfileDir, ref uint lpcchSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
