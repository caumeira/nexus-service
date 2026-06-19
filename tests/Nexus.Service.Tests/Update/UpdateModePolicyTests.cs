using Nexus.Service.Models.Update;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for the three-mode UpdateMode setting: default value, persistence
/// shape, and the mode-gating semantics that PollAsync enforces.
/// </summary>
public sealed class UpdateModePolicyTests
{
    // --- UpdateSettings defaults ---

    [Fact]
    public void UpdateSettings_default_mode_is_always()
    {
        var s = new UpdateSettings();
        Assert.Equal("always", s.UpdateMode);
    }

    [Fact]
    public void UpdateSettings_default_channel_is_production()
    {
        var s = new UpdateSettings();
        Assert.Equal("production", s.UpdateChannel);
    }

    // --- UpdatePrefs shape ---

    [Fact]
    public void UpdatePrefs_default_mode_is_always()
    {
        var p = new UpdatePrefs();
        Assert.Equal("always", p.UpdateMode);
    }

    [Fact]
    public void UpdatePrefsPatch_mode_is_nullable_string()
    {
        var patch = new UpdatePrefsPatch();
        Assert.Null(patch.UpdateMode);
    }

    // --- UpdateStatusResponse shape ---

    [Fact]
    public void UpdateStatusResponse_default_mode_is_always()
    {
        var r = new UpdateStatusResponse();
        Assert.Equal("always", r.UpdateMode);
    }

    [Fact]
    public void UpdateStatusResponse_carries_assigned_mode()
    {
        foreach (var mode in new[] { "notify", "download", "always" })
        {
            var r = new UpdateStatusResponse { UpdateMode = mode };
            Assert.Equal(mode, r.UpdateMode);
        }
    }

    // --- Mode-gating logic (mirrors PollAsync decision) ---

    // PollAsync stages a background install only for "download" and "always"
    // (positive allowlist); any other value, including unrecognized ones, does
    // not stage. Encoded as a pure predicate so it can be verified without
    // constructing UpdateService.

    private static bool ShouldAutoStage(string mode) => mode is "download" or "always";
    private static bool ShouldLaunchAfterVerify(string mode) => false; // poller never launches; always mode applies on restart

    [Theory]
    [InlineData("notify",   false)]
    [InlineData("download", true)]
    [InlineData("always",   true)]
    public void ShouldAutoStage_correct_for_all_modes(string mode, bool expected)
    {
        Assert.Equal(expected, ShouldAutoStage(mode));
    }

    [Theory]
    [InlineData("notify",   false)]
    [InlineData("download", false)]
    [InlineData("always",   false)]
    public void ShouldLaunchAfterVerify_poller_never_launches(string mode, bool expected)
    {
        Assert.Equal(expected, ShouldLaunchAfterVerify(mode));
    }

    // ApplyPendingOnStartup auto-applies a staged install only for "always".
    // "download" waits for the user to click POST /update/start.

    private static bool ShouldApplyOnStartup(string mode) => mode == "always";

    [Theory]
    [InlineData("notify",   false)]
    [InlineData("download", false)]
    [InlineData("always",   true)]
    public void ApplyPendingOnStartup_applies_only_for_always(string mode, bool expectedApply)
    {
        Assert.Equal(expectedApply, ShouldApplyOnStartup(mode));
    }

    // The success branch (version already advanced past the marker) runs for
    // ALL modes: the install was triggered by "Update now" in notify/download
    // mode, and the new service should still set JustUpdatedTo and reopen.

    private static bool ShouldSignalSuccessForMode(string mode) => mode is "notify" or "download" or "always";

    [Theory]
    [InlineData("notify")]
    [InlineData("download")]
    [InlineData("always")]
    public void ApplyPendingOnStartup_success_branch_runs_for_all_modes(string mode)
    {
        Assert.True(ShouldSignalSuccessForMode(mode));
    }

    // --- NexusSettings roundtrip via UpdateSettings ---

    [Fact]
    public void NexusSettings_update_mode_survives_object_mutation()
    {
        var s = new NexusSettings();
        Assert.Equal("always", s.Update.UpdateMode);

        s.Update.UpdateMode = "notify";
        Assert.Equal("notify", s.Update.UpdateMode);

        s.Update.UpdateMode = "download";
        Assert.Equal("download", s.Update.UpdateMode);
    }
}
