#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// The one place that decides whether a DDC/CI transaction may go out right
/// now. Every transaction in <see cref="WindowsDisplayBrightnessProvider"/>
/// passes through <see cref="ShouldSkip"/>, so the policy holds whoever the
/// caller is - the Displays widget's 5 s poll, a Stream Deck key, an SDK app
/// action, or the dashboard.
///
/// Why it exists: a monitor entering, sitting in, or leaving DPMS-off can hang
/// its scaler on a DDC transaction, and its OSD and power button then stop
/// responding until the panel is physically unplugged. A session lock is the
/// case that bites. Unlike sleep, nothing suspends this process: Windows powers
/// the monitors down behind the lock screen while we keep talking to them on
/// the caller's cadence. Reported on a Samsung Odyssey G7 (SAM105C) over
/// DisplayPort and confirmed by A/B - our traffic removed, no hang; restored,
/// the hang returns.
///
/// Lock state is queried rather than tracked from transitions: this runs in the
/// helper, which can ask WTS about its own session directly, and the query
/// costs nothing next to the DDC round-trip it guards.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DdcGate
{
    /// <summary>
    /// How long after an observed unlock to stay quiet. The desktop is back
    /// before the panels have finished re-training their links, and a
    /// transaction landing in that window is the same hazard as one landing
    /// during the blank.
    /// </summary>
    private static readonly TimeSpan UnlockSettle = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static bool _lastLocked;
    private static DateTime _unlockedAtUtc = DateTime.MinValue;

    /// <summary>
    /// True when no DDC transaction should be issued right now.
    /// <paramref name="reason"/> is a short tag for the log, empty when the
    /// call is allowed.
    /// </summary>
    public static bool ShouldSkip(out string reason)
    {
        var locked = QuerySessionLocked();
        DateTime unlockedAt;
        lock (Gate)
        {
            // Only a transition we actually observed opens the settle window. A
            // first call that finds the session already unlocked has no
            // transition to be near, so it is not held back.
            if (_lastLocked && !locked) _unlockedAtUtc = DateTime.UtcNow;
            _lastLocked = locked;
            unlockedAt = _unlockedAtUtc;
        }

        if (locked)
        {
            reason = "session locked";
            return true;
        }

        if (unlockedAt != DateTime.MinValue && DateTime.UtcNow - unlockedAt < UnlockSettle)
        {
            reason = "unlock settling";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// WTS lock state for this process's own session. UNKNOWN (0xFFFFFFFF) and
    /// every failure path read as unlocked: a query that cannot answer must not
    /// disable brightness control forever.
    /// </summary>
    private static bool QuerySessionLocked()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx, out var buf, out var len)
                || buf == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (len < (uint)Marshal.SizeOf<WTSINFOEX_PREFIX>()) return false;
                var info = Marshal.PtrToStructure<WTSINFOEX_PREFIX>(buf);
                return info.Level == 1 && info.SessionFlags == WTS_SESSIONSTATE_LOCK;
            }
            finally { WTSFreeMemory(buf); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ddc] lock-state query failed: {ex.Message}");
            return false;
        }
    }

    private const uint WTS_CURRENT_SESSION = unchecked((uint)-1);
    // WTS_INFO_CLASS.WTSSessionInfoEx
    private const int WTSSessionInfoEx = 25;
    // WTSINFOEX_LEVEL1_W.SessionFlags, Win10+ semantics (the documented Win7
    // lock/unlock inversion predates the 19041 floor). UNKNOWN is 0xFFFFFFFF.
    private const int WTS_SESSIONSTATE_LOCK = 0;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    // Leading fields of WTSINFOEXW (x64): Level at 0; the Data union starts at
    // 8 because WTSINFOEX_LEVEL1_W carries LARGE_INTEGER members (8-byte
    // alignment). Only the fields ahead of the union's WCHAR arrays are mapped.
    [StructLayout(LayoutKind.Explicit)]
    private struct WTSINFOEX_PREFIX
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }
}
#endif
