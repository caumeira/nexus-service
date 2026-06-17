using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Tests for ChromaShimInstaller.DecideFile, the pure deploy-decision logic.
/// Covers: missing -> copy, current -> skip, updated -> overwrite, real Razer -> conflict.
/// </summary>
public class ChromaShimInstallerTests
{
    // ── File not present ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Copy_When_Destination_Missing()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: false,
            destFileDescription: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Copy, decision);
    }

    [Fact]
    public void DecideFile_Returns_Copy_When_Destination_Missing_Regardless_Of_ContentMatches()
    {
        // contentMatches=true is nonsensical when destExists=false, but the
        // function should still return Copy (the dest doesn't exist).
        var decision = ChromaShimInstaller.DecideFile(
            destExists: false,
            destFileDescription: "",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Copy, decision);
    }

    // ── Real Razer DLL present ───────────────────────────────────────────────

    [Theory]
    [InlineData("Razer Chroma SDK")]
    [InlineData("Razer Chroma SDK Service")]
    [InlineData("Razer USA Ltd.")]
    [InlineData("some other product")]
    public void DecideFile_Returns_Conflict_When_Destination_Has_Non_Nexus_Description(string description)
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: description,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    [Fact]
    public void DecideFile_Returns_Conflict_Even_When_Content_Would_Match_But_Description_Is_Foreign()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: "Razer Chroma SDK",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    // ── Our shim already installed ───────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_Our_Shim_And_Content_Matches()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: ChromaShimInstaller.OurFileDescription,
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_OurFileDescription_Case_Insensitive_Matches_Skip()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: ChromaShimInstaller.OurFileDescription.ToLowerInvariant(),
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    // ── Our shim outdated ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Overwrite_When_Our_Shim_But_Content_Differs()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: ChromaShimInstaller.OurFileDescription,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── Empty FileDescription (unsigned or stripped DLL) ─────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_Description_Empty_And_Content_Matches()
    {
        // A DLL with no FileDescription is treated as our shim if content matches.
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: "",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_Returns_Overwrite_When_Description_Empty_And_Content_Differs()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destFileDescription: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── DLL name constants ────────────────────────────────────────────────────

    [Fact]
    public void X64Names_Contains_Both_Required_DLLs()
    {
        Assert.Contains("RzChromaSDK64.dll", ChromaShimInstaller.X64Names);
        Assert.Contains("RzChromatic64.dll", ChromaShimInstaller.X64Names);
    }

    [Fact]
    public void X86Names_Contains_Both_Required_DLLs()
    {
        Assert.Contains("RzChromaSDK.dll", ChromaShimInstaller.X86Names);
        Assert.Contains("RzChromatic.dll", ChromaShimInstaller.X86Names);
    }

    // ── Non-Windows returns NotApplicable ─────────────────────────────────────

    [Fact]
    public void EnsureInstalled_Returns_NotApplicable_On_NonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // only meaningful to run on non-Windows
        }

        var result = ChromaShimInstaller.EnsureInstalled();
        Assert.Equal(ChromaShimInstallResult.NotApplicable, result);
    }

    [Fact]
    public void GetState_Returns_Empty_State_On_NonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var state = ChromaShimInstaller.GetState();
        Assert.False(state.ProviderInstalled);
        Assert.False(state.SynapseConflict);
    }
}
