using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// The in-app "drop into DFU bootloader" handshake, ported from HYTE's
/// nexus-control-service <c>OTAHelper.Update</c>. Sent over a device's normal
/// serial channel (the same <see cref="INp50Transport"/> the firmware-version
/// hubs use):
///
///   1. Write the OTA product key (<c>FF DC 06 …</c>) into the boot-flag region.
///   2. Verify it stuck via the <c>FF DC 07</c> readback (retry like the legacy).
///   3. Write the DFU magic (<c>FF AA 09 08 07 06 05</c>) — the device reboots
///      into its DFU bootloader and re-enumerates as VID 3402 / PID 0A00.
///
/// Caller is responsible for turning LEDs off first and for waiting on the
/// DFU re-enumeration afterwards. This step is destructive to the running
/// application (the device leaves normal mode) — only invoke it as part of a
/// confirmed flash.
/// </summary>
public static class OtaDfuEntry
{
    private static readonly byte[] DfuMagic = { 0xFF, 0xAA, 0x09, 0x08, 0x07, 0x06, 0x05 };
    private static readonly byte[] ReadKeyCmd = { 0xFF, 0xDC, 0x07 };
    private const int KeyVerifyAttempts = 5;
    private const int ReadTimeoutMs = 200;

    /// <summary>
    /// Run the handshake. Returns false (without sending the DFU magic) if the
    /// product key can't be written/verified, so we never reboot a device into
    /// DFU with a key that wouldn't accept the image.
    /// </summary>
    public static bool Enter(INp50Transport transport, byte[] productKey, bool verifyKey = true)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(productKey);

        transport.DiscardInput();
        transport.Write(productKey);

        if (verifyKey && !VerifyKey(transport, productKey))
            return false;

        // Point of no return: device reboots into the bootloader after this.
        transport.Write(DfuMagic);
        return true;
    }

    /// <summary>
    /// Read back the stored product key (<c>FF DC 07</c>) and confirm bytes
    /// [3..6] match what we wrote, rewriting + retrying like
    /// <c>OTAHelper.CheckPid</c>.
    /// </summary>
    public static bool VerifyKey(INp50Transport transport, byte[] productKey)
    {
        for (var attempt = 0; attempt < KeyVerifyAttempts; attempt++)
        {
            transport.DiscardInput();
            transport.Write(ReadKeyCmd);
            var buf = new byte[7];
            var n = transport.Read(buf, ReadTimeoutMs);
            if (n >= 7
                && buf[3] == productKey[3]
                && buf[4] == productKey[4]
                && buf[5] == productKey[5]
                && buf[6] == productKey[6])
            {
                return true;
            }
            // Rewrite the key and try again.
            transport.Write(productKey);
        }
        return false;
    }
}
