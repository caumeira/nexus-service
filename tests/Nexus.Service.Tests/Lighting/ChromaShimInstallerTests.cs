using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Tests for ChromaShimInstaller.DecideFile, the pure deploy-decision logic.
/// Covers: missing -> copy, current -> skip, updated -> overwrite, real vendor DLL -> conflict.
/// </summary>
public class ChromaShimInstallerTests
{
    // ── File not present ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Copy_When_Destination_Missing()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: false,
            destCompanyName: "",
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
            destCompanyName: "",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Copy, decision);
    }

    // ── Real vendor DLL present ───────────────────────────────────────────────

    [Theory]
    [InlineData("Razer Inc.")]
    [InlineData("Razer USA Ltd.")]
    [InlineData("Logitech")]
    [InlineData("some other company")]
    public void DecideFile_Returns_Conflict_When_Destination_Has_Non_Nexus_CompanyName(string companyName)
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: companyName,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    [Fact]
    public void DecideFile_Returns_Conflict_Even_When_Content_Would_Match_But_CompanyName_Is_Foreign()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "Razer Inc.",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    // ── Our shim already installed ───────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_Our_Shim_And_Content_Matches()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: ChromaShimInstaller.OurCompanyName,
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_OurCompanyName_Case_Insensitive_Matches_Skip()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: ChromaShimInstaller.OurCompanyName.ToLowerInvariant(),
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    // ── Our shim outdated ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Overwrite_When_Our_Shim_But_Content_Differs()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: ChromaShimInstaller.OurCompanyName,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── Empty CompanyName (unsigned or stripped DLL) ─────────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_CompanyName_Empty_And_Content_Matches()
    {
        // A DLL with no CompanyName is treated as our shim if content matches.
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_Returns_Overwrite_When_CompanyName_Empty_And_Content_Differs()
    {
        var decision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── DLL name constants ────────────────────────────────────────────────────

    [Fact]
    public void X64Names_Contains_All_Required_DLLs()
    {
        Assert.Contains("RzChromaSDK64.dll", ChromaShimInstaller.X64Names);
        Assert.Contains("RzChromatic64.dll", ChromaShimInstaller.X64Names);
        Assert.Contains("LightFX.dll", ChromaShimInstaller.X64Names);
        Assert.Contains("LogitechLedEnginesWrapper.dll", ChromaShimInstaller.X64Names);
        Assert.Contains("LogitechLed.dll", ChromaShimInstaller.X64Names);
    }

    [Fact]
    public void X86Names_Contains_All_Required_DLLs()
    {
        Assert.Contains("RzChromaSDK.dll", ChromaShimInstaller.X86Names);
        Assert.Contains("RzChromatic.dll", ChromaShimInstaller.X86Names);
        Assert.Contains("LightFX.dll", ChromaShimInstaller.X86Names);
        Assert.Contains("LogitechLedEnginesWrapper.dll", ChromaShimInstaller.X86Names);
        Assert.Contains("LogitechLed.dll", ChromaShimInstaller.X86Names);
    }

    // ── Per-file conflict does not block other slots ───────────────────────────

    [Fact]
    public void DecideFile_Conflict_On_One_File_Does_Not_Affect_Independent_Decisions()
    {
        // A vendor DLL in one slot must not affect the decision for a different slot.
        var conflictDecision = ChromaShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "Logitech",
            contentMatches: false);

        var copyDecision = ChromaShimInstaller.DecideFile(
            destExists: false,
            destCompanyName: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Conflict, conflictDecision);
        Assert.Equal(ChromaShimFileDecision.Copy, copyDecision);
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
