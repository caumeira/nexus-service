using System.Buffers.Binary;

namespace Nexus.Service.Diagnostics.Storage;

/// <summary>
/// Parsed NVMe Health Information log page (log page 02h, NVMe Base
/// Specification 1.4). 512 bytes, little-endian. Byte offsets used below:
///
///   byte    0      CriticalWarning              bitmask
///   bytes   1-2    CompositeTemperature          Kelvin, uint16
///   byte    3      AvailableSpare                percent
///   byte    4      AvailableSpareThreshold       percent
///   byte    5      PercentageUsed                percent, can exceed 100
///   bytes   32-47  DataUnitsRead                 128-bit, units of 512000 bytes
///   bytes   48-63  DataUnitsWritten              128-bit, units of 512000 bytes
///   bytes  112-127 PowerCycles                   128-bit
///   bytes  128-143 PowerOnHours                  128-bit
///   bytes  144-159 UnsafeShutdowns               128-bit
///   bytes  160-175 MediaAndDataIntegrityErrors   128-bit
///   bytes  176-191 ErrorInfoLogEntries           128-bit
///
/// Every 128-bit field is reduced to its low 64 bits (ulong.MaxValue if the
/// high 64 are ever nonzero) since no real drive counter reaches that range.
/// </summary>
public sealed record NvmeHealthLog
{
    public required byte CriticalWarning { get; init; }
    public required ushort CompositeTemperatureKelvin { get; init; }
    public required byte AvailableSpare { get; init; }
    public required byte AvailableSpareThreshold { get; init; }
    public required byte PercentageUsed { get; init; }
    public required ulong DataUnitsReadBytes { get; init; }
    public required ulong DataUnitsWrittenBytes { get; init; }
    public required ulong PowerCycles { get; init; }
    public required ulong PowerOnHours { get; init; }
    public required ulong UnsafeShutdowns { get; init; }
    public required ulong MediaErrors { get; init; }
    public required ulong ErrorInfoLogEntries { get; init; }

    private const ulong BytesPerDataUnit = 512_000;
    private const int LogLength = 512;

    public static NvmeHealthLog? Parse(byte[]? data)
    {
        if (data is null || data.Length < LogLength) return null;
        var span = data.AsSpan();

        return new NvmeHealthLog
        {
            CriticalWarning = span[0],
            CompositeTemperatureKelvin = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1, 2)),
            AvailableSpare = span[3],
            AvailableSpareThreshold = span[4],
            PercentageUsed = span[5],
            DataUnitsReadBytes = ReadUnitsAsBytes(span, 32),
            DataUnitsWrittenBytes = ReadUnitsAsBytes(span, 48),
            PowerCycles = ReadLower64WithGuard(span, 112),
            PowerOnHours = ReadLower64WithGuard(span, 128),
            UnsafeShutdowns = ReadLower64WithGuard(span, 144),
            MediaErrors = ReadLower64WithGuard(span, 160),
            ErrorInfoLogEntries = ReadLower64WithGuard(span, 176),
        };
    }

    private static ulong ReadLower64WithGuard(System.ReadOnlySpan<byte> data, int offset)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
        var high = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
        return high != 0 ? ulong.MaxValue : low;
    }

    private static ulong ReadUnitsAsBytes(System.ReadOnlySpan<byte> data, int offset)
    {
        var units = ReadLower64WithGuard(data, offset);
        if (units == 0) return 0;
        return units > ulong.MaxValue / BytesPerDataUnit ? ulong.MaxValue : units * BytesPerDataUnit;
    }
}
