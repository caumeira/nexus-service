using Nexus.Service.Update;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for UpdateService.CanOfferUpdate (the download-tier offer decision,
/// which every platform gets once an asset resolves for it) and
/// UpdateService.ShouldAutoStage (the background download+install gate, which
/// stays Windows-only since RunInstallAsync only implements the install
/// handoff there).
/// </summary>
public class UpdateAvailabilityGateTests
{
    [Fact]
    public void CanOfferUpdate_True_WhenNewer()
    {
        Assert.True(UpdateService.CanOfferUpdate(isNewer: true));
    }

    [Fact]
    public void CanOfferUpdate_False_WhenNotNewer()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false));
    }

    [Theory]
    [InlineData("always")]
    [InlineData("download")]
    public void ShouldAutoStage_True_OnWindows_WhenOfferedAndNotStaged(string mode)
    {
        Assert.True(UpdateService.ShouldAutoStage(offerUpdate: true, mode, alreadyStaged: false, isWindows: true));
    }

    [Theory]
    [InlineData("always")]
    [InlineData("download")]
    public void ShouldAutoStage_False_OffWindows_EvenWhenOfferedAndNotStaged(string mode)
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode, alreadyStaged: false, isWindows: false));
    }

    [Fact]
    public void ShouldAutoStage_False_InNotifyMode_EvenOnWindows()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode: "notify", alreadyStaged: false, isWindows: true));
    }

    [Fact]
    public void ShouldAutoStage_False_WhenAlreadyStaged()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode: "always", alreadyStaged: true, isWindows: true));
    }

    [Fact]
    public void ShouldAutoStage_False_WhenNotOffered()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: false, mode: "always", alreadyStaged: false, isWindows: true));
    }
}
