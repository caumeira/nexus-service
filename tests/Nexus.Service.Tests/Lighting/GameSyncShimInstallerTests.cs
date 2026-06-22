using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Tests for GameSyncShimInstaller.DecideFile, the pure deploy-decision logic.
/// Covers: missing -> copy, current -> skip, updated -> overwrite, real vendor DLL -> conflict.
/// </summary>
public class GameSyncShimInstallerTests
{
    // ── File not present ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Copy_When_Destination_Missing()
    {
        var decision = GameSyncShimInstaller.DecideFile(
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
        var decision = GameSyncShimInstaller.DecideFile(
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
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: companyName,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    [Fact]
    public void DecideFile_Returns_Conflict_Even_When_Content_Would_Match_But_CompanyName_Is_Foreign()
    {
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "Razer Inc.",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Conflict, decision);
    }

    // ── Our shim already installed ───────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_Our_Shim_And_Content_Matches()
    {
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: GameSyncShimInstaller.OurCompanyName,
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_OurCompanyName_Case_Insensitive_Matches_Skip()
    {
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: GameSyncShimInstaller.OurCompanyName.ToLowerInvariant(),
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    // ── Our shim outdated ────────────────────────────────────────────────────

    [Fact]
    public void DecideFile_Returns_Overwrite_When_Our_Shim_But_Content_Differs()
    {
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: GameSyncShimInstaller.OurCompanyName,
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── Empty CompanyName (unsigned or stripped DLL) ─────────────────────────

    [Fact]
    public void DecideFile_Returns_Skip_When_CompanyName_Empty_And_Content_Matches()
    {
        // A DLL with no CompanyName is treated as our shim if content matches.
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "",
            contentMatches: true);

        Assert.Equal(ChromaShimFileDecision.Skip, decision);
    }

    [Fact]
    public void DecideFile_Returns_Overwrite_When_CompanyName_Empty_And_Content_Differs()
    {
        var decision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Overwrite, decision);
    }

    // ── Per-file conflict does not block other slots ───────────────────────────

    [Fact]
    public void DecideFile_Conflict_On_One_File_Does_Not_Affect_Independent_Decisions()
    {
        // A vendor DLL in one slot must not affect the decision for a different slot.
        var conflictDecision = GameSyncShimInstaller.DecideFile(
            destExists: true,
            destCompanyName: "Logitech",
            contentMatches: false);

        var copyDecision = GameSyncShimInstaller.DecideFile(
            destExists: false,
            destCompanyName: "",
            contentMatches: false);

        Assert.Equal(ChromaShimFileDecision.Conflict, conflictDecision);
        Assert.Equal(ChromaShimFileDecision.Copy, copyDecision);
    }

    // ── Non-Windows returns NotApplicable ─────────────────────────────────────

    [NonWindowsFact]
    public void EnsureInstalled_Returns_NotApplicable_On_NonWindows()
    {
        var result = GameSyncShimInstaller.EnsureInstalled();
        Assert.Equal(ChromaShimInstallResult.NotApplicable, result);
    }

    [NonWindowsFact]
    public void GetState_Returns_Empty_State_On_NonWindows()
    {
        var state = GameSyncShimInstaller.GetState();
        Assert.False(state.ProviderInstalled);
        Assert.False(state.SynapseConflict);
    }
}
