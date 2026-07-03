using System;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdProtocolTests
{
    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x1CBE, Slv3LcdProtocol.VendorId);
        Assert.Equal(0x0005, Slv3LcdProtocol.ProductIdSl);
        Assert.Equal(0x0006, Slv3LcdProtocol.ProductIdTl);
        Assert.Equal(0x01, Slv3LcdProtocol.WritePipeId);
        Assert.Equal(0x81, Slv3LcdProtocol.ReadPipeId);
        Assert.Equal(400, Slv3LcdProtocol.PanelWidth);
        Assert.Equal(400, Slv3LcdProtocol.PanelHeight);
        Assert.Equal(102400, Slv3LcdProtocol.TransferBufferSize);
        Assert.Equal(504, Slv3LcdProtocol.HeaderPlainSize);
        Assert.Equal(512, Slv3LcdProtocol.HeaderCipherSize);
        Assert.Equal(new byte[] { 115, 108, 118, 51, 116, 117, 122, 120 }, Slv3LcdProtocol.DesKeyIv);
    }

    [Fact]
    public void CmdType_values_match_known_values()
    {
        Assert.Equal(10, (int)Slv3LcdProtocol.CmdType.GetVer);
        Assert.Equal(11, (int)Slv3LcdProtocol.CmdType.Reboot);
        Assert.Equal(13, (int)Slv3LcdProtocol.CmdType.Rotate);
        Assert.Equal(14, (int)Slv3LcdProtocol.CmdType.BrigthSet);
        Assert.Equal(15, (int)Slv3LcdProtocol.CmdType.SetFrameRate);
        Assert.Equal(40, (int)Slv3LcdProtocol.CmdType.UpdateFireWare);
        Assert.Equal(101, (int)Slv3LcdProtocol.CmdType.PushJpg);
        Assert.Equal(201, (int)Slv3LcdProtocol.CmdType.GetPosIndex);
    }

    // ── BuildLengthHeader (PushJpg) byte layout ──

    [Fact]
    public void BuildLengthHeader_lays_out_cmd_magic_timestamp_and_length()
    {
        var header = Slv3LcdProtocol.BuildLengthHeader(Slv3LcdProtocol.CmdType.PushJpg, 0x11223344, 44936);

        Assert.Equal(504, header.Length);
        Assert.Equal(101, header[0]);
        Assert.Equal(0, header[1]);
        Assert.Equal(0x1A, header[2]);
        Assert.Equal(0x6D, header[3]);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, header.AsSpan(4, 4).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0xAF, 0x88 }, header.AsSpan(8, 4).ToArray());
        // remaining bytes untouched (zero)
        for (var i = 12; i < header.Length; i++)
        {
            Assert.Equal(0, header[i]);
        }
    }

    // ── BuildArgHeader (Rotate/BrigthSet/SetFrameRate) byte layout ──

    [Fact]
    public void BuildArgHeader_places_arg_byte_at_offset_8_only()
    {
        var header = Slv3LcdProtocol.BuildArgHeader(Slv3LcdProtocol.CmdType.BrigthSet, 0x11223344, 77);

        Assert.Equal(504, header.Length);
        Assert.Equal(14, header[0]);
        Assert.Equal(0, header[1]);
        Assert.Equal(0x1A, header[2]);
        Assert.Equal(0x6D, header[3]);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, header.AsSpan(4, 4).ToArray());
        Assert.Equal(77, header[8]);
        // no length field written past the single arg byte
        Assert.Equal(0, header[9]);
        Assert.Equal(0, header[10]);
        Assert.Equal(0, header[11]);
    }

    // ── DES-CBC/PKCS7 vector, independently derived and cross-checked (see
    //    plans/lianli-wireless-support.md section 4.1 for the algorithm facts) ──

    private const string ExpectedCipherHex =
        "03CF7A6ABF5E82E9D87A0CF2DBE981B276605D24DDB16EA13E0E9F69CA3A81BD9EDC55C50EC85C6344EAFBBC8B5F7BF" +
        "C300BA103E86D4FC5C25D5E211D58B97A2B8F1841B64E17CAA761589515ED82D5C0AA79293A13B5CD2B04A2D122C6B7" +
        "0F9334176E5CE87B152C6A0BCC39B7863169921D7D5C3FED12391A54938D5A0018EAC34AD0B21F6418E7B91F3115EA0" +
        "C5B82271BF9E79603A10BE6A9F068BDDBCB3F8C02DF91BC31551D26FEAB93317F315284EA3073A09CBE9F767E2B655B7" +
        "4AAE79C3672EB4AC33718F29AAD6E178A0CBDD7DB139A0A560E2AB939AC831E252F5F21806CA9A25CA5A46FBB507C6D9" +
        "6BB0810F2D75D350782F50D91A3C16067D3B2474FB950CC66D8F91816B715382C2B1E37F71ABD65F5F5250D4C22F095" +
        "6DAB12BCD2F6609420C8E42867632B1DCB4AEC6175BFBD3A48F772B1611519B244C83729982F2080EF789992FA3706C" +
        "A58EBF3FF834A1F38520EC63A8BF4F26E4096F2DD35FD066732C66E6F3902CC2CD4BEBDEB8EAE56A0D8E68BCAF0EE4B2" +
        "4184D7FE9880143F452CA6D645602D439C7264215E65575122B842C91F2AA1364BC196BD0BEBDB7102F6AB4D65CBDB1" +
        "3891A6E81125BB604E86BB003D4492FF5B544708DF0AB974463B87F50DE77EEBF34CAEEC874F0BDB1959304FB045538" +
        "12AF28DE3F4E82A802E305DC37EFBF10EE23C2FD0258F78D26FD4625153DC593AADFECB";

    // The plaintext header from which the vector above was derived carries
    // header[8..11] = 0x00,0x00,0xAF,0x88; that BE32 value is 44936, not the
    // 45000 figure noted alongside it in the spec (a documentation slip in
    // the decimal annotation, not in the byte values, cross-checked against
    // the byte pattern via an independent openssl des-ecb chained-by-hand
    // computation as well as this test's own DES.Create() path).
    [Fact]
    public void EncryptHeader_matches_known_des_cbc_vector()
    {
        var plainHeader = Slv3LcdProtocol.BuildLengthHeader(Slv3LcdProtocol.CmdType.PushJpg, 0x11223344, 44936);

        var encrypted = Slv3LcdProtocol.EncryptHeader(plainHeader);

        Assert.Equal(512, encrypted.Length);
        Assert.Equal(Convert.FromHexString(ExpectedCipherHex), encrypted);
    }

    // ── BuildTransferBuffer assembly ──

    [Fact]
    public void BuildTransferBuffer_places_header_then_jpeg_then_zero_pad()
    {
        var header = new byte[512];
        Array.Fill(header, (byte)0xEE);
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        var buffer = Slv3LcdProtocol.BuildTransferBuffer(header, jpeg);

        Assert.Equal(102400, buffer.Length);
        Assert.Equal(header, buffer.AsSpan(0, 512).ToArray());
        Assert.Equal(jpeg, buffer.AsSpan(512, jpeg.Length).ToArray());
        Assert.Equal(0, buffer[512 + jpeg.Length]);
        Assert.Equal(0, buffer[^1]);
    }

    [Fact]
    public void BuildTransferBuffer_throws_when_jpeg_would_not_fit()
    {
        var header = new byte[512];
        var jpeg = new byte[102400 - 512 + 1];
        Assert.Throws<ArgumentException>(() => Slv3LcdProtocol.BuildTransferBuffer(header, jpeg));
    }

    [Fact]
    public void BuildPushJpgBuffer_total_size_and_jpeg_offset()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0x03 };
        var buffer = Slv3LcdProtocol.BuildPushJpgBuffer(0x11223344, jpeg);

        Assert.Equal(102400, buffer.Length);
        Assert.Equal(jpeg, buffer.AsSpan(512, jpeg.Length).ToArray());
    }

    [Fact]
    public void BuildArgCommandHeader_returns_encrypted_512_bytes()
    {
        var header = Slv3LcdProtocol.BuildArgCommandHeader(Slv3LcdProtocol.CmdType.Rotate, 0x11223344, 2);
        Assert.Equal(512, header.Length);
    }
}
