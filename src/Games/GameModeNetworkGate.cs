namespace Nexus.Service.Games;

/// <summary>
/// Ambient "background egress is deferred" flag, written by
/// <see cref="GameModeEffects"/> and read by the periodic cloud workers.
///
/// Only retry-on-next-tick work consults it, so a skipped tick loses nothing;
/// user-initiated network work ignores it entirely.
/// </summary>
public static class GameModeNetworkGate
{
    private static volatile bool _held;

    public static bool IsHeld => _held;

    public static void Set(bool held) => _held = held;

    internal static void ResetForTests() => _held = false;
}
