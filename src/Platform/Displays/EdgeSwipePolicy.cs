namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Windows edge-swipe suppression for hosts driving a touch panel display:
/// a swipe from the glass edge otherwise opens the Windows widgets board /
/// notification center over the kiosk.
/// </summary>
public interface IEdgeSwipePolicy
{
    /// <summary>Idempotent: writes the disable value only when it is not
    /// already in force. Returns true when the registry changed; the policy
    /// applies at the next explorer.exe start (logon or explorer restart).</summary>
    bool EnsureDisabled();
}
