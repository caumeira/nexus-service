using System.Threading;

namespace Nexus.Service.QSeries;

/// <summary>
/// Cross-thread "act on this next tick" signal from a request thread to the
/// tick loop; one instance per kind of request (display settings, link repair).
///
/// A counter rather than a 0/1 flag because the tick takes the signal before
/// its device loop but reads state inside it, and the work in between spends
/// ~1.7s in one `input keyevent`. An exchange-to-zero flag was swallowed by
/// anything landing in that window - tapping the screen switch off then
/// straight back on left the panel dark until a re-attach, since the tick had
/// already cleared the flag and re-latched the serial.
/// </summary>
internal sealed class TickChangeSignal
{
    private long _announced;

    /// <summary>Read and written only by the tick thread.</summary>
    private long _seen;

    internal void Announce() => Interlocked.Increment(ref _announced);

    /// <summary>
    /// True when at least one announcement landed since the last call,
    /// including one made while the previous tick was mid-apply. Tick-thread
    /// only.
    /// </summary>
    internal bool Take()
    {
        var announced = Interlocked.Read(ref _announced);
        if (announced == _seen) return false;
        _seen = announced;
        return true;
    }
}
