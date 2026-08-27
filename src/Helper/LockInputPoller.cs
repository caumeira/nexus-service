#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Reports "someone touched this machine" while a lock blackout is up, so the
/// lighting can come back for whoever is standing there typing a password.
///
/// <c>GetLastInputInfo</c> is the only input signal that survives the lock
/// screen. Low-level keyboard hooks cannot see the Winlogon secure desktop,
/// and Windows denies user-mode reads of HID collections that are keyboards or
/// mice, but the session's last-input tick keeps advancing across the lock -
/// measured on T1 2026-08-27, seven distinct input timestamps over a locked
/// window while <c>OpenInputDesktop</c> was failing. It is session-scoped,
/// which is why it is read here: the Session 0 service would only ever see its
/// own idle time.
///
/// It carries no key identity, and that is the point. The keys being pressed
/// at a credential prompt are a password, so all that leaves this class is
/// that input happened - never what it was.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LockInputPoller : IDisposable
{
    /// <summary>Fast enough that the ramp is already running while the finger is still on the key.</summary>
    private const int PollPeriodMs = 250;

    /// <summary>
    /// The service only needs its idle window refreshed, so continuous typing
    /// costs one envelope a second instead of one per poll.
    /// </summary>
    private const int EmitThrottleMs = 1000;

    /// <summary>
    /// Input in the first moments after arming is the tail of the gesture that
    /// locked the machine - the key release of Win+L, the hand leaving the
    /// mouse - and reporting it woke the lighting 165ms into its own fade-out
    /// (measured on T1). Covers the 1500ms lock ramp plus settle, so nothing is
    /// reported until the machine has actually finished going dark.
    /// </summary>
    private const int ArmGraceMs = 2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly HelperOutbound _outbound;
    private readonly JitteredPeriodicTimer _timer;

    private volatile bool _armed;
    private uint _lastSeenTick;
    private long _lastEmitMs;
    private long _armedAtMs;

    public LockInputPoller(HelperOutbound outbound)
    {
        _outbound = outbound;
        _timer = new JitteredPeriodicTimer(PollPeriodMs, jitterMs: 0, Poll);
    }

    /// <summary>
    /// Arm or disarm from the service's <c>lighting.lockInputWatch</c>. Arming
    /// re-baselines first: the keystroke that locked the machine is itself
    /// input, and reporting it would wake the lights the instant they went dark.
    /// </summary>
    public void SetArmed(bool armed)
    {
        if (armed == _armed) return;
        if (armed)
        {
            _lastSeenTick = QueryLastInputTick();
            _armedAtMs = Environment.TickCount64;
        }
        _armed = armed;
    }

    private void Poll()
    {
        if (!_armed) return;
        try
        {
            var tick = QueryLastInputTick();
            if (tick == _lastSeenTick) return;
            _lastSeenTick = tick;

            var now = Environment.TickCount64;
            // Still inside the grace window: the baseline has moved with the
            // gesture above, so nothing that happened during it is reported
            // once the window closes.
            if (now - _armedAtMs < ArmGraceMs) return;
            if (now - _lastEmitMs < EmitThrottleMs) return;
            _lastEmitMs = now;

            _ = _outbound.SendAsync(
                type: LockLightingCommands.InputSeenType,
                payload: new LockInputSeenPayload(),
                payloadType: AppJsonContext.Default.LockInputSeenPayload);
        }
        catch { /* best-effort; the next poll is 250ms away */ }
    }

    private static uint QueryLastInputTick()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
    }

    public void Dispose() => _timer.Dispose();
}
#endif
