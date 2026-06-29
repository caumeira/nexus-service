namespace Nexus.Service.Peripherals.LianLi;

public enum LianLiFanFamily
{
    Sl,
    Al,
    SlInfinity,
    SlV2,
    AlV2,
}

/// <summary>
/// Per-PID fan descriptor. Parameterises the Uni fan builders; fields map
/// directly to protocol byte positions. Source: FanControl.LianLi / L-Connect 3.
/// </summary>
public readonly struct LianLiFanProfile
{
    public LianLiFanFamily Family { get; init; }

    /// <summary>
    /// When true, duty 0 maps to 1 and duty 1..9 maps to 10 (firmware stall floor).
    /// When false, duty passes through clamped to 0..100.
    /// </summary>
    public bool FlooredDuty { get; init; }

    /// <summary>Byte 2 of the manual-mode command (E0 10 &lt;reg&gt; ...).</summary>
    public byte ManualRegister { get; init; }

    /// <summary>Byte 2 of the ARGB/effect channel selector command.</summary>
    public byte ArgbRegister { get; init; }

    /// <summary>Base offset into the RPM input report: rpm[ch] = BE16(buf[RpmOffset + ch*2]).</summary>
    public int RpmOffset { get; init; }

    public string? ModelName { get; init; }
}

/// <summary>
/// PID-keyed profile table for the Lian Li Uni fan family (vendor 0x0CF2).
/// Source: FanControl.LianLi / L-Connect 3.
/// </summary>
public static class LianLiFanProfiles
{
    private static readonly (int Pid, LianLiFanProfile Profile)[] s_table =
    {
        (0x7750, new LianLiFanProfile { Family = LianLiFanFamily.Sl,         FlooredDuty = false, ManualRegister = 49, ArgbRegister = 48, RpmOffset = 1, ModelName = "Uni Hub" }),
        (0xA100, new LianLiFanProfile { Family = LianLiFanFamily.Sl,         FlooredDuty = false, ManualRegister = 49, ArgbRegister = 48, RpmOffset = 1, ModelName = "Uni SL" }),
        (0xA101, new LianLiFanProfile { Family = LianLiFanFamily.Al,         FlooredDuty = false, ManualRegister = 66, ArgbRegister = 65, RpmOffset = 1, ModelName = "Uni AL" }),
        (0xA102, new LianLiFanProfile { Family = LianLiFanFamily.SlInfinity, FlooredDuty = true,  ManualRegister = 98, ArgbRegister = 97, RpmOffset = 1, ModelName = "SL-Infinity" }),
        (0xA103, new LianLiFanProfile { Family = LianLiFanFamily.SlV2,       FlooredDuty = true,  ManualRegister = 98, ArgbRegister = 97, RpmOffset = 2, ModelName = "Uni SL v2" }),
        (0xA104, new LianLiFanProfile { Family = LianLiFanFamily.AlV2,       FlooredDuty = true,  ManualRegister = 98, ArgbRegister = 97, RpmOffset = 2, ModelName = "Uni AL v2" }),
        (0xA105, new LianLiFanProfile { Family = LianLiFanFamily.SlV2,       FlooredDuty = true,  ManualRegister = 98, ArgbRegister = 97, RpmOffset = 2, ModelName = "Uni SL v2" }),
        (0xA106, new LianLiFanProfile { Family = LianLiFanFamily.Sl,         FlooredDuty = false, ManualRegister = 49, ArgbRegister = 48, RpmOffset = 1, ModelName = "Uni SL (Redragon OEM)" }),
    };

    /// <summary>All Uni fan PIDs for HID enumeration.</summary>
    public static readonly int[] AllProductIds =
    {
        0x7750, 0xA100, 0xA101, 0xA102, 0xA103, 0xA104, 0xA105, 0xA106,
    };

    public static bool TryGet(int productId, out LianLiFanProfile profile)
    {
        foreach (var (pid, p) in s_table)
        {
            if (pid == productId)
            {
                profile = p;
                return true;
            }
        }
        profile = default;
        return false;
    }
}
