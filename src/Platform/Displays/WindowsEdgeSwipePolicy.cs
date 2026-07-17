#if WINDOWS
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// The "Allow edge swipe" machine policy. 0 disables the Windows screen-edge
/// swipe overlays (widgets board / notification center) machine-wide;
/// explorer reads it at start, so a change applies at the next logon or
/// explorer restart. One-way by design: the panel leaving does not restore
/// edge swipes (matching how the legacy onboarding left the machine).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEdgeSwipePolicy : IEdgeSwipePolicy
{
    private const string KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI";
    private const string ValueName = "AllowEdgeSwipe";

    public bool EnsureDisabled()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        if (key.GetValue(ValueName) is int existing && existing == 0) return false;
        key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        return true;
    }
}
#endif
