using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>Daemon verbosity override: only -v and -vv raise its stdout, and an
/// argument it rejects makes it print help rather than serve.</summary>
public class OpenRgbVerbosityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_AddsNoFlag(string? configured)
    {
        Assert.Null(OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("v")]
    [InlineData("  VERBOSE  ")]
    public void Verbose_MapsToSingleFlag(string configured)
    {
        Assert.Equal("-v", OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("vv")]
    [InlineData("Trace")]
    public void Trace_MapsToDoubleFlag(string configured)
    {
        Assert.Equal("-vv", OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }

    [Fact]
    public void Debug_MapsToDoubleFlag()
    {
        // No flag targets LL_DEBUG on its own, and -v stops one level short of it.
        Assert.Equal("-vv", OpenRgbProcessManager.ResolveVerbosityFlag("debug"));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("6")]
    [InlineData("-vvv")]
    [InlineData("--server")]
    public void UnrecognizedValues_AddNoFlag(string configured)
    {
        Assert.Null(OpenRgbProcessManager.ResolveVerbosityFlag(configured));
    }
}
