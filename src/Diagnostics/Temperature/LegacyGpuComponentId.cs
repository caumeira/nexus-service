using System;

namespace Nexus.Service.Diagnostics.Temperature;

/// <summary>
/// The enumeration-index GPU id scheme (gpu:0, gpu:1, ...); an NVML UUID id
/// always contains a hyphen, so an all-digit suffix after "gpu:" unambiguously
/// identifies this scheme.
/// </summary>
public static class LegacyGpuComponentId
{
    public static bool IsLegacy(string componentId)
    {
        if (!componentId.StartsWith("gpu:", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = componentId.AsSpan(4);
        if (suffix.IsEmpty)
        {
            return false;
        }

        foreach (var c in suffix)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}
