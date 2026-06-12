using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Service.Webcam.Linux;

/// <summary>
/// V4L2 ABI surface needed to drive a v4l2loopback output device. Constants
/// and struct layouts mirror linux/videodev2.h on 64-bit targets: the fmt
/// union inside struct v4l2_format contains pointer-bearing arms
/// (struct v4l2_window), so on LP64 it is pointer-aligned, which fixes both
/// the union offset and the total size baked into the VIDIOC_S_FMT ioctl
/// number. The service ships 64-bit Linux builds only.
/// </summary>
internal static class V4l2
{
    /// <summary>enum v4l2_buf_type: V4L2_BUF_TYPE_VIDEO_OUTPUT.</summary>
    public const uint BufTypeVideoOutput = 2;

    /// <summary>enum v4l2_field: V4L2_FIELD_NONE (progressive).</summary>
    public const uint FieldNone = 1;

    /// <summary>V4L2_PIX_FMT_MJPEG fourcc.</summary>
    public const uint PixFmtMjpeg = 'M' | ((uint)'J' << 8) | ((uint)'P' << 16) | ((uint)'G' << 24);

    /// <summary>VIDIOC_S_FMT = _IOWR('V', 5, struct v4l2_format).</summary>
    public static readonly nuint VidiocSFmt = Iowr('V', 5, (uint)Unsafe.SizeOf<V4l2Format>());

    // _IOC(dir,type,nr,size) packs dir into the top bits, then size, type, nr.
    private const uint IocWrite = 1;
    private const uint IocRead = 2;

    private static nuint Iowr(char type, uint nr, uint size) =>
        (nuint)(((IocRead | IocWrite) << 30) | (size << 16) | ((uint)type << 8) | nr);
}

/// <summary>Mirrors struct v4l2_pix_format (videodev2.h). All fields are u32.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct V4l2PixFormat
{
    public uint Width;
    public uint Height;
    /// <summary>fourcc</summary>
    public uint PixelFormat;
    /// <summary>enum v4l2_field</summary>
    public uint Field;
    public uint BytesPerLine;
    public uint SizeImage;
    /// <summary>enum v4l2_colorspace</summary>
    public uint Colorspace;
    public uint Priv;
    public uint Flags;
    /// <summary>anonymous union { ycbcr_enc, hsv_enc }</summary>
    public uint YcbcrEnc;
    public uint Quantization;
    public uint XferFunc;
}

/// <summary>
/// Mirrors struct v4l2_format (videodev2.h), fmt.pix arm only. The raw_data
/// arm fixes the union size; the pointer-bearing arms fix its alignment on
/// 64-bit, which places the union after padding and pads the total size.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = TotalSize)]
internal struct V4l2Format
{
    internal const int UnionOffset = 8;
    internal const int UnionSize = 200;
    internal const int TotalSize = UnionOffset + UnionSize;

    /// <summary>enum v4l2_buf_type</summary>
    [FieldOffset(0)] public uint Type;

    /// <summary>fmt.pix arm of the union</summary>
    [FieldOffset(UnionOffset)] public V4l2PixFormat Pix;
}

/// <summary>
/// Seam over the libc fd calls and device enumeration so
/// <see cref="V4l2LoopbackCamera"/> logic is unit-testable off-Linux.
/// </summary>
internal interface IV4l2DeviceIo
{
    /// <summary>v4l2loopback devices visible on the host: (dev node name, sysfs card name).</summary>
    IEnumerable<(string Device, string CardName)> EnumerateVideoDevices();

    /// <summary>Open the device read-write; negative on failure.</summary>
    int Open(string path);

    /// <summary>VIDIOC_S_FMT.</summary>
    bool SetFormat(int fd, ref V4l2Format format);

    /// <summary>Bytes written, negative on failure.</summary>
    int Write(int fd, ReadOnlySpan<byte> payload);

    void Close(int fd);
}

/// <summary>Real libc-backed IO. AOT-safe: blittable LibraryImport, no reflection.</summary>
internal sealed partial class LibcV4l2DeviceIo : IV4l2DeviceIo
{
    /// <summary>v4l2loopback registers its devices under the virtual video4linux sysfs class.</summary>
    internal const string SysfsRoot = "/sys/devices/virtual/video4linux";

    public IEnumerable<(string Device, string CardName)> EnumerateVideoDevices()
    {
        if (!Directory.Exists(SysfsRoot))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(SysfsRoot))
        {
            string cardName;
            try
            {
                cardName = File.ReadAllText(Path.Combine(dir, "name"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            yield return (Path.GetFileName(dir), cardName);
        }
    }

    public int Open(string path) => open(path, O_RDWR);

    public bool SetFormat(int fd, ref V4l2Format format)
    {
        unsafe
        {
            fixed (V4l2Format* p = &format)
                return ioctl(fd, V4l2.VidiocSFmt, (nint)p) >= 0;
        }
    }

    public int Write(int fd, ReadOnlySpan<byte> payload)
    {
        unsafe
        {
            fixed (byte* p = payload)
                return (int)write(fd, (nint)p, (nuint)payload.Length);
        }
    }

    public void Close(int fd) => close(fd);

    private const int O_RDWR = 2;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, nint arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int fd, nint buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
