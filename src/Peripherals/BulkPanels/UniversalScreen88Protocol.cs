using System;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8 (1CBE:A088), a 480x1920 strip on a bulk pipe.
///
/// Every command is a 500-byte plaintext block DES-encrypted in CBC mode, then padded out
/// to a 512-byte packet with a two-byte magic tail. The key and the IV are both the ASCII
/// string "slv3tuzx". Reconstructed from third-party documentation; no unit has been run
/// against it.
///
/// The name hints this panel shares a firmware vendor with the Turzx LCD family.
/// </summary>
public static class UniversalScreen88Protocol
{
    public const int Width = 480;
    public const int Height = 1920;

    /// <summary>Plaintext block size before encryption and padding.</summary>
    public const int PlainLength = 500;

    /// <summary>Encrypted block plus six zeros and the two-byte tail.</summary>
    public const int PacketLength = 512;

    /// <summary>Most a command can carry after the 8-byte preamble.</summary>
    public const int MaxParams = PlainLength - 8;

    public const byte CommandGetVersion = 0x0A;
    public const byte CommandBrightness = 0x0E;
    public const byte CommandFrameRate = 0x0F;
    public const byte CommandStopClock = 0x34;
    public const byte CommandPushJpeg = 0x65;
    public const byte CommandStopPlay = 0x7B;

    /// <summary>ASCII "slv3tuzx", used as both the DES key and the CBC IV.</summary>
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("slv3tuzx");

    /// <summary>Closes every packet; the firmware rejects a block without it.</summary>
    private static ReadOnlySpan<byte> Tail => new byte[] { 0xA1, 0x1A };

    /// <summary>
    /// Builds one 512-byte command packet. <paramref name="timestampSeconds"/> must strictly
    /// increase across calls - it is documented as clamped to at least the previous value
    /// plus one, so the panel evidently rejects or ignores a repeat.
    /// </summary>
    public static byte[] EncodeCommand(byte command, ReadOnlySpan<byte> parameters, uint timestampSeconds)
    {
        if (parameters.Length > MaxParams)
        {
            throw new ArgumentException($"at most {MaxParams} parameter bytes", nameof(parameters));
        }
        var plain = new byte[PlainLength];
        plain[0] = command;
        plain[2] = 0x1A;
        plain[3] = 0x6D;
        plain[4] = (byte)(timestampSeconds & 0xFF);
        plain[5] = (byte)((timestampSeconds >> 8) & 0xFF);
        plain[6] = (byte)((timestampSeconds >> 16) & 0xFF);
        plain[7] = (byte)((timestampSeconds >> 24) & 0xFF);
        parameters.CopyTo(plain.AsSpan(8));

        var encrypted = Encrypt(plain);
        var packet = new byte[PacketLength];
        encrypted.CopyTo(packet.AsSpan());
        // Six zeros between the ciphertext and the tail; the array is already zeroed.
        Tail.CopyTo(packet.AsSpan(PacketLength - Tail.Length));
        return packet;
    }

    /// <summary>
    /// Header for a frame push. The JPEG length rides the parameters big-endian, and the
    /// caller sends this packet followed by the JPEG in one transfer.
    /// </summary>
    public static byte[] EncodeFrameHeader(int jpegLength, uint timestampSeconds)
    {
        Span<byte> length = stackalloc byte[4];
        length[0] = (byte)((jpegLength >> 24) & 0xFF);
        length[1] = (byte)((jpegLength >> 16) & 0xFF);
        length[2] = (byte)((jpegLength >> 8) & 0xFF);
        length[3] = (byte)(jpegLength & 0xFF);
        return EncodeCommand(CommandPushJpeg, length, timestampSeconds);
    }

    /// <summary>
    /// The opening sequence: read the version, set brightness to 0x32, stop whatever the
    /// panel was playing, stop its clock face, then pin the frame rate to 0x3C.
    /// </summary>
    public static byte[][] EncodeInitSequence(Func<uint> nextTimestamp) => new[]
    {
        EncodeCommand(CommandGetVersion, ReadOnlySpan<byte>.Empty, nextTimestamp()),
        EncodeCommand(CommandBrightness, new byte[] { 0x32, 0x00, 0x00, 0x00 }, nextTimestamp()),
        EncodeCommand(CommandStopPlay, ReadOnlySpan<byte>.Empty, nextTimestamp()),
        EncodeCommand(CommandStopClock, ReadOnlySpan<byte>.Empty, nextTimestamp()),
        EncodeCommand(CommandFrameRate, new byte[] { 0x3C }, nextTimestamp()),
    };

    /// <summary>
    /// DES-CBC with PKCS#7 padding, key and IV both the ASCII key. 500 bytes in, 504 out.
    /// DES is long broken as a cipher; it is used here only because it is what the panel's
    /// firmware implements, and the key is public.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
#pragma warning disable CA5351 // Broken cryptographic algorithm - dictated by the device firmware.
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = Key;
        des.IV = Key;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var encryptor = des.CreateEncryptor();
        var input = plain.ToArray();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }
}
