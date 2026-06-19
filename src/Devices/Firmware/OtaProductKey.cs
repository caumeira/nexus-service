using System;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Builds the per-device OTA product-key command (<c>FF DC 06 …</c>) that the
/// in-app DFU handshake writes into the bootloader's flag region. The
/// bootloader checks this key to decide whether to accept a firmware image, so
/// it must match the physical device.
///
/// The key encodes the device's operating USB PID: byte[3]=PID high, byte[4]=PID
/// low, then <c>00 DD</c>. Confirmed against HYTE's <c>USBDevicesFactory</c>
/// PIDBytes table - Q60 0400→…04 00, Q80 0403→…04 03, NP50 0901→…09 01,
/// MiniHub 0900→…09 00, CNVS Left 0B00→…0B 00, Y70 Infinite 0C01→…0C 01, etc.
/// </summary>
public static class OtaProductKey
{
    /// <summary>The <c>FF DC 06 hi lo 00 DD</c> product-key write command for a device's operating PID.</summary>
    public static byte[] ForProductId(int productId)
    {
        if (productId is < 0 or > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(productId), productId, "USB product id must be a 16-bit value.");
        return new byte[] { 0xFF, 0xDC, 0x06, (byte)((productId >> 8) & 0xFF), (byte)(productId & 0xFF), 0x00, 0xDD };
    }
}
