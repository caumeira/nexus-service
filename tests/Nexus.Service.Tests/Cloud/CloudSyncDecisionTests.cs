using Nexus.Service.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

public sealed class CloudSyncDecisionTests
{
    [Fact]
    public void Neither_side_has_the_profile_is_none()
    {
        var action = CloudSyncDecision.Decide(
            localExists: false, localHash: null,
            cloudExists: false, cloudRevision: 0,
            hasSyncRecord: false, syncedRevision: 0, syncedHash: null);
        Assert.Equal(CloudSyncAction.None, action);
    }

    [Fact]
    public void Local_only_profile_pushes()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h1",
            cloudExists: false, cloudRevision: 0,
            hasSyncRecord: false, syncedRevision: 0, syncedHash: null);
        Assert.Equal(CloudSyncAction.Push, action);
    }

    [Fact]
    public void Cloud_only_profile_pulls()
    {
        var action = CloudSyncDecision.Decide(
            localExists: false, localHash: null,
            cloudExists: true, cloudRevision: 3,
            hasSyncRecord: false, syncedRevision: 0, syncedHash: null);
        Assert.Equal(CloudSyncAction.Pull, action);
    }

    [Fact]
    public void Local_deleted_after_having_synced_deletes_remote()
    {
        var action = CloudSyncDecision.Decide(
            localExists: false, localHash: null,
            cloudExists: true, cloudRevision: 5,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.DeleteRemote, action);
    }

    [Fact]
    public void Cloud_deleted_on_another_machine_after_having_synced_deletes_local()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h1",
            cloudExists: false, cloudRevision: 0,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.DeleteLocal, action);
    }

    // Signing out drops the sync records, so this is the ordinary sign-in-again
    // path, not a rare one. Pulling here would overwrite whatever the user did
    // locally in the meantime with the last backup.
    [Fact]
    public void Both_exist_with_no_sync_record_pushes_so_local_wins()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h1",
            cloudExists: true, cloudRevision: 2,
            hasSyncRecord: false, syncedRevision: 0, syncedHash: null);
        Assert.Equal(CloudSyncAction.Push, action);
    }

    [Fact]
    public void Clean_local_and_unchanged_cloud_is_none()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h1",
            cloudExists: true, cloudRevision: 5,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.None, action);
    }

    [Fact]
    public void Clean_local_and_cloud_moved_pulls_silent_lww()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h1",
            cloudExists: true, cloudRevision: 6,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.Pull, action);
    }

    [Fact]
    public void Dirty_local_and_unchanged_cloud_pushes_silent_lww()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h2",
            cloudExists: true, cloudRevision: 5,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.Push, action);
    }

    [Fact]
    public void Dirty_local_and_cloud_moved_is_a_conflict()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: "h2",
            cloudExists: true, cloudRevision: 6,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.Conflict, action);
    }

    // Decide compares localHash to syncedHash by plain string equality, with
    // no special-casing for null. A profile that was never hashed on either
    // side (both null - a profile synced before LastSyncedHash existed, or an
    // export that failed on both occasions) reads as "clean" by that
    // comparison; what actually matters is that a null localHash can never
    // match a REAL (non-null) syncedHash, so an export failure on a profile
    // that previously synced successfully is treated as dirty, not silently
    // clean.
    [Fact]
    public void Null_local_hash_matches_only_an_equally_null_synced_hash()
    {
        var action = CloudSyncDecision.Decide(
            localExists: true, localHash: null,
            cloudExists: true, cloudRevision: 5,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: null);
        Assert.Equal(CloudSyncAction.None, action);

        var actionWithHash = CloudSyncDecision.Decide(
            localExists: true, localHash: null,
            cloudExists: true, cloudRevision: 5,
            hasSyncRecord: true, syncedRevision: 5, syncedHash: "h1");
        Assert.Equal(CloudSyncAction.Push, actionWithHash);
    }
}
