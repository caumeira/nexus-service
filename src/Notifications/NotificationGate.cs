using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Service.Notifications;

/// <summary>
/// Holds user-facing notifications back while first-run onboarding is still
/// going, then releases them in order once it completes.
///
/// Onboarding screens are full-viewport modals that own focus, so a notification
/// raised behind one cannot be acted on - clicking it appears to do nothing.
/// Held, not dropped: a staged update or a pending pair request still reaches
/// the user, at the first moment they can respond to it.
/// </summary>
public static class NotificationGate
{
    // Enough for a slow first run; past that the oldest are dropped rather than
    // burying the user in a backlog the moment onboarding ends.
    private const int MaxHeld = 16;

    private static readonly object Lock = new();
    private static readonly Queue<Func<Task>> Held = new();
    private static bool _releasing;
    private static bool _open;

    /// <summary>True once notifications flow straight through.</summary>
    public static bool IsOpen { get { lock (Lock) { return _open; } } }

    /// <summary>Sets the initial state from persisted onboarding status at startup.</summary>
    public static void Initialize(bool onboardingComplete)
    {
        lock (Lock)
        {
            _open = onboardingComplete;
            if (_open) Held.Clear();
        }
    }

    /// <summary>
    /// Runs <paramref name="send"/> now when open, otherwise holds it. Returns
    /// true when it was held, so callers can skip their own logging.
    /// </summary>
    public static bool HoldOrRun(Func<Task> send, out Task task)
    {
        lock (Lock)
        {
            if (!_open)
            {
                if (Held.Count >= MaxHeld) Held.Dequeue();
                Held.Enqueue(send);
                task = Task.CompletedTask;
                return true;
            }
        }
        task = send();
        return false;
    }

    /// <summary>Opens the gate and flushes anything held, oldest first.</summary>
    public static async Task ReleaseAsync()
    {
        List<Func<Task>> pending;
        lock (Lock)
        {
            if (_open || _releasing) return;
            _releasing = true;
            _open = true;
            pending = new List<Func<Task>>(Held);
            Held.Clear();
        }
        try
        {
            foreach (var send in pending)
            {
                try { await send().ConfigureAwait(false); }
                catch { /* one bad notice must not strand the rest */ }
            }
        }
        finally
        {
            lock (Lock) { _releasing = false; }
        }
    }

    /// <summary>Test hook: returns the gate to its pre-onboarding state.</summary>
    internal static void ResetForTests()
    {
        lock (Lock) { _open = false; _releasing = false; Held.Clear(); }
    }

    internal static int HeldCount { get { lock (Lock) { return Held.Count; } } }
}
