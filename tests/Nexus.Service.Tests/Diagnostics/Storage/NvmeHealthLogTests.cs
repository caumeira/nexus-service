using System.Buffers.Binary;
using Nexus.Service.Diagnostics.Storage;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Storage;

public class NvmeHealthLogTests
{
    [Fact]
    public void Parse_returns_null_for_null_input()
    {
        Assert.Null(NvmeHealthLog.Parse(null));
    }

    [Fact]
    public void Parse_returns_null_for_short_input()
    {
        Assert.Null(NvmeHealthLog.Parse(new byte[100]));
    }

    [Fact]
    public void Parse_reads_every_field_at_its_spec_offset()
    {
        var data = new byte[512];
        data[0] = 0x07; // CriticalWarning
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1, 2), 315); // CompositeTemperature (Kelvin)
        data[3] = 100; // AvailableSpare
        data[4] = 10; // AvailableSpareThreshold
        data[5] = 3; // PercentageUsed
        WriteUInt128(data, 32, 1000); // DataUnitsRead: 1000 units
        WriteUInt128(data, 48, 2000); // DataUnitsWritten: 2000 units
        WriteUInt128(data, 112, 456); // PowerCycles
        WriteUInt128(data, 128, 1234); // PowerOnHours
        WriteUInt128(data, 144, 12); // UnsafeShutdowns
        WriteUInt128(data, 160, 0); // MediaErrors
        WriteUInt128(data, 176, 7); // ErrorInfoLogEntries

        var log = NvmeHealthLog.Parse(data);

        Assert.NotNull(log);
        Assert.Equal(0x07, log!.CriticalWarning);
        Assert.Equal(315, log.CompositeTemperatureKelvin);
        Assert.Equal(100, log.AvailableSpare);
        Assert.Equal(10, log.AvailableSpareThreshold);
        Assert.Equal(3, log.PercentageUsed);
        Assert.Equal(1000UL * 512_000UL, log.DataUnitsReadBytes);
        Assert.Equal(2000UL * 512_000UL, log.DataUnitsWrittenBytes);
        Assert.Equal(456UL, log.PowerCycles);
        Assert.Equal(1234UL, log.PowerOnHours);
        Assert.Equal(12UL, log.UnsafeShutdowns);
        Assert.Equal(0UL, log.MediaErrors);
        Assert.Equal(7UL, log.ErrorInfoLogEntries);
    }

    [Fact]
    public void Parse_clamps_a_128_bit_field_whose_high_64_bits_are_nonzero()
    {
        var data = new byte[512];
        // PowerOnHours: low 64 bits = 1, high 64 bits nonzero - no real drive
        // reaches this, but the parser must not silently wrap.
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(128, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(136, 8), 1);

        var log = NvmeHealthLog.Parse(data);

        Assert.NotNull(log);
        Assert.Equal(ulong.MaxValue, log!.PowerOnHours);
    }

    [Fact]
    public void Parse_clamps_data_units_bytes_on_multiplication_overflow()
    {
        var data = new byte[512];
        // A units count whose *512000 conversion would overflow ulong must clamp,
        // not wrap, even though the 128-bit-to-64-bit guard alone would pass it.
        WriteUInt128(data, 32, ulong.MaxValue / 512_000 + 1);

        var log = NvmeHealthLog.Parse(data);

        Assert.NotNull(log);
        Assert.Equal(ulong.MaxValue, log!.DataUnitsReadBytes);
    }

    private static void WriteUInt128(byte[] data, int offset, ulong low)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), low);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 8, 8), 0);
    }
}
