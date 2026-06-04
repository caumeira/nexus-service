using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class SpecsParsingTests
{
    [Theory]
    [InlineData("32 GB DDR5-6000 (2 × 16 GB Crucial CP16G60C48U5.M8B2)", 32)]
    [InlineData("31.9 GB", 32)]
    [InlineData("16 GB", 16)]
    [InlineData("64 GB DDR5", 64)]
    public void ParseRamGb_takes_the_first_GB_figure(string input, int expected)
        => Assert.Equal(expected, SystemProfileService.ParseRamGb(input));

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void ParseRamGb_null_when_no_GB_figure(string input)
        => Assert.Null(SystemProfileService.ParseRamGb(input));

    [Fact]
    public void ParseStorageGb_single_TB_drive_reported_in_GiB()
        => Assert.Equal(1864, SystemProfileService.ParseStorageGb("1.82 TB WDS200T1X0E-00AFY0 NVMe"));

    [Fact]
    public void ParseStorageGb_sums_every_drive()
        => Assert.Equal(1864 + 931, SystemProfileService.ParseStorageGb("1.82 TB Samsung 990 + 931 GB WD Blue"));

    [Fact]
    public void ParseStorageGb_ignores_model_number_digits()
        // "WDS200T1X0E" has no TB/GB unit after the digits, so they don't count.
        => Assert.Equal(500, SystemProfileService.ParseStorageGb("500 GB WDS200T1X0E"));

    [Fact]
    public void ParseStorageGb_ignores_capacity_embedded_in_a_model_name()
        // Only the leading per-drive capacity counts, not a size baked into the model.
        => Assert.Equal(931, SystemProfileService.ParseStorageGb("931 GB WD Blue SN570 1TB"));

    [Theory]
    [InlineData("")]
    [InlineData("SomeDrive NVMe")]
    public void ParseStorageGb_null_when_no_capacity(string input)
        => Assert.Null(SystemProfileService.ParseStorageGb(input));
}
