using System;
using Nexus.Service.Peripherals.Hyte.Cnvs;

namespace Nexus.Service.Tests.Cnvs;

/// <summary>
/// Golden-vector coverage for the CNVS settings wire protocol. Bytes
/// here come from HYTE's nexus-control-service CNVSHelper.cs (the
/// shipping vendor reference), not from the spec doc table — there
/// isn't a separate spec for these commands. Same testing posture as
/// Np50ProtocolTests.cs.
/// </summary>
public class CnvsProtocolTests
{
    [Theory]
    [InlineData(false, false, 0x00, 0x00)]
    [InlineData(true,  false, 0x01, 0x00)]
    [InlineData(false, true,  0x00, 0x01)]
    [InlineData(true,  true,  0x01, 0x01)]
    public void BuildSetSettings_emits_FF_DC_07_with_both_flags(
        bool suppress, bool keepOn, byte expectedSuppress, byte expectedKeepOn)
    {
        var bytes = CnvsProtocol.BuildSetSettings(suppress, keepOn);
        Assert.Equal(new byte[] { 0xFF, 0xDC, 0x07, expectedSuppress, expectedKeepOn }, bytes);
    }

    [Fact]
    public void BuildGetSettings_emits_FF_DC_08()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDC, 0x08 }, CnvsProtocol.BuildGetSettings());
    }

    [Fact]
    public void BuildGetFirmwareVersion_emits_FF_DD_02()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x02 }, CnvsProtocol.BuildGetFirmwareVersion());
    }

    [Fact]
    public void BuildTurnAnimationOff_emits_FF_DC_05_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDC, 0x05, 0x00 }, CnvsProtocol.BuildTurnAnimationOff());
    }

    [Fact]
    public void BuildTurnAnimationOnPreamble_emits_FF_DC_02()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDC, 0x02 }, CnvsProtocol.BuildTurnAnimationOnPreamble());
    }

    [Fact]
    public void BuildTurnAnimationOnMain_emits_FF_DC_05_01()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDC, 0x05, 0x01 }, CnvsProtocol.BuildTurnAnimationOnMain());
    }

    [Theory]
    [InlineData(0x00, 0x00, false, false)]
    [InlineData(0x01, 0x00, true,  false)]
    [InlineData(0x00, 0x01, false, true)]
    [InlineData(0x01, 0x01, true,  true)]
    public void ParseGetSettings_reads_bytes_3_and_4(
        byte suppressByte, byte keepOnByte, bool expectedSuppress, bool expectedKeepOn)
    {
        // 9-byte response per CNVSHelper.GetCnvsSettingFromFW (reads command[3] and command[4]).
        var response = new byte[]
        {
            0xFF, 0xDC, 0x08,
            suppressByte, keepOnByte,
            0x00, 0x00, 0x00, 0x00,
        };
        var parsed = CnvsProtocol.ParseGetSettings(response);
        Assert.Equal(expectedSuppress, parsed.SuppressBootAnimation);
        Assert.Equal(expectedKeepOn, parsed.KeepLedsOnWhenPcOff);
    }

    [Fact]
    public void ParseGetSettings_rejects_short_response()
    {
        Assert.Throws<ArgumentException>(() => CnvsProtocol.ParseGetSettings(new byte[] { 0xFF, 0xDC }));
    }

    [Fact]
    public void ParseFirmwareVersion_returns_dotted_string()
    {
        var response = new byte[] { 0xFF, 0xDD, 0x02, 1, 2, 3, 4 };
        Assert.Equal("1.2.3.4", CnvsProtocol.ParseFirmwareVersion(response));
    }
}
