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
    /// Input in the first moment after arming is the tail of the gesture that
    /// locked the machine - the key release of Win+L, the hand leaving the
    /// mouse - and reporting it woke the lighting 165ms into its own fade-out
    /// (measured on T1), which is what this window is sized against. It stays
    /// well under the 1500ms lock ramp on purpose: a real touch during the tail
    /// of that ramp is still honoured, because BeginBlackoutFade marks the hold
    /// engaged as the ramp starts, so the release picks up from the dimmed
    /// level rather than jumping to full.
    /// </summary>
    private const int ArmGraceMs = 500;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly HelperOutbound _outbound;
    private readonly object _gate = new();

    /// <summary>Exists only while armed; see <see cref="SetArmed"/>.</summary>
    private JitteredPeriodicTimer? _timer;

    private volatile bool _armed;
    private uint _lastSeenTick;
    private long _lastEmitMs;
    private long _armedAtMs;

    public LockInputPoller(HelperOutbound outbound) => _outbound = outbound;

    /// <summary>
    /// Arm or disarm from the service's <c>lighting.lockInputWatch</c>. Arming
    /// re-baselines first: the keystroke that locked the machine is itself
    /// input, and reporting it would wake the lights the instant they went dark.
    /// </summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            if (armed)
            {
                // Re-baseline on every arm, not only on a transition. A disarm
                // lost to a helper reconnect would otherwise leave this armed,
                // and the next lock would reuse a stale baseline and report the
                // locking gesture as a wake.
                if (TryQueryLastInputTick(out var tick)) _lastSeenTick = tick;
                _armedAtMs = Environment.TickCount64;
                _armed = true;
                // Only ticking while a lock blackout is up: the other helper
                // pollers run at 2s or slower, and this one is four times a
                // second.
                _timer ??= new JitteredPeriodicTimer(PollPeriodMs, jitterMs: 0, Poll);
                return;
            }
            _armed = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        if (!_armed) return;
        if (HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.LockInput)) return;
        try
        {
            // A failed call must not stand in for a tick: reporting the
            // sentinel, then reporting the recovery back to a real tick, would
            // light an unattended locked machine twice over.
            if (!TryQueryLastInputTick(out var tick)) return;
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

    private static bool TryQueryLastInputTick(out uint tick)
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
        {
            tick = 0;
            return false;
        }
        tick = lii.dwTime;
        return true;
    }

    public void Dispose() => SetArmed(false);
}
#endif
