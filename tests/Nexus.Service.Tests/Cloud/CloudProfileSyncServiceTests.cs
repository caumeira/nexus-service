using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>Real ProfileManager + JsonConfigStore against a temp dir (matching Integration/ProfileSwitchTests.cs), a FakeCloudApiClient standing in for api.hellonexus.com, and CloudProfileSyncService's internal methods invoked directly (bypassing the BackgroundService loop, which only matters for scheduling, not the sync logic itself).</summary>
public sealed class CloudProfileSyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeCloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly CloudProfileSyncService _sync;
    private readonly ManualTimeProvider _clock;

    public CloudProfileSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-cloud-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();

        _api = new FakeCloudApiClient();
        _clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _accounts = new CloudAccountService(_api, _store, _clock);
        _sync = new CloudProfileSyncService(_api, _accounts, _profiles, _store, _clock);
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void SeedAccount(string accountId, string refreshToken)
    {
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = accountId, RefreshToken = refreshToken });
            s.Auth.ActiveCloudAccountId = accountId;
        });
        _api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-" + accountId,
            RefreshToken = refreshToken,
            Account = new CloudAccountDto { Id = accountId, Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    // ── conflict resolution ──────────────────────────────────────────────

    [Fact]
    public async Task RunSyncPass_detects_a_conflict_when_local_is_dirty_and_cloud_moved_past_the_synced_base()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;

        var baseExport = _profiles.ExportProfileForSync(profileId)!;
        var baseHash = CloudProfileSyncService.HashPayload(baseExport);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord
        { Revision = 1, LastSyncedHash = baseHash, LastSyncedAt = "2026-01-01T00:00:00Z" });

        // Local edit after the last sync - now dirty relative to baseHash.
        _store.Update(s => s.Lighting.GlobalBrightness = 0.2f);
        _store.FlushNow();

        // Cloud also moved past revision 1 in the meantime.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "2026-01-02T00:00:00Z", UpdatedByInstallId = "other-machine" },
        });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        var status = _sync.GetStatus();
        Assert.Equal("dirty", status.State);
        var conflict = Assert.Single(status.Conflicts);
        Assert.Equal(profileId, conflict.ProfileId);
        Assert.Equal(2, conflict.CloudRevision);
        Assert.Equal("other-machine", conflict.UpdatedByInstallId);

        // Neither side was clobbered - no push, no pull happened automatically.
        Assert.Equal(0, _api.PutProfileCalls);
        Assert.Equal(0, _api.GetProfileCalls);
    }

    [Fact]
    public async Task ResolveConflict_choice_local_pushes_using_the_conflicting_cloud_revision_as_base()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _store.FlushNow();
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Single(_sync.GetStatus().Conflicts);

        int? capturedBaseRevision = null;
        _api.OnPutProfile = (_, _, body) =>
        {
            capturedBaseRevision = body.BaseRevision;
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 3 });
        };

        var result = await _sync.ResolveConflictAsync(profileId, "local", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, capturedBaseRevision); // the CURRENT (conflicting) cloud revision, not the stale synced one.
        Assert.Empty(_sync.GetStatus().Conflicts);
        Assert.Equal(3, _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].Revision);
    }

    [Fact]
    public async Task ResolveConflict_choice_cloud_overwrites_local_with_the_cloud_payload()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _store.FlushNow();
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Single(_sync.GetStatus().Conflicts);

        var cloudSettings = new NexusSettings();
        cloudSettings.Lighting.GlobalBrightness = 0.9f;
        _api.OnGetProfile = (_, _) => CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
        {
            ProfileId = profileId,
            Name = "Default",
            Revision = 2,
            Payload = new ProfileExport { Name = "Default", Settings = cloudSettings },
        });

        var result = await _sync.ResolveConflictAsync(profileId, "cloud", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(_sync.GetStatus().Conflicts);
        Assert.Equal(0.9f, _store.Load().Lighting.GlobalBrightness);
        Assert.Equal(2, _store.Load().Auth!.CloudAccounts[0].ProfileSync[profileId].Revision);
    }

    [Fact]
    public async Task ResolveConflict_invalid_choice_is_rejected()
    {
        SeedAccount("acct-1", "refresh-1");
        var profileId = _profiles.GetActiveEntry()!.Id;
        var baseHash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(profileId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[profileId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = baseHash });
        _store.Update(s => s.Lighting.GlobalBrightness = 0.3f);
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = profileId, Name = "Default", Revision = 2, UpdatedAt = "t", UpdatedByInstallId = "other" },
        });
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        var result = await _sync.ResolveConflictAsync(profileId, "sideways", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("invalid_choice", result.ErrorCode);
    }

    // ── delete propagation ───────────────────────────────────────────────

    [Fact]
    public async Task RunSyncPass_deletes_remote_when_local_was_deleted_after_syncing()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id;
        var deletedId = _profiles.CreateProfile("ToDelete").Id;
        _profiles.SwitchProfile(defaultId);

        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(deletedId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[deletedId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });
        _profiles.DeleteProfile(deletedId); // local delete - a second, still-present profile exists.

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = deletedId, Name = "ToDelete", Revision = 1 },
        });
        string? deletedProfileId = null;
        _api.OnDeleteProfile = (_, profileId) => { deletedProfileId = profileId; return CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance); };

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(deletedId, deletedProfileId);
        Assert.Equal(1, _api.DeleteProfileCalls);
        Assert.DoesNotContain(deletedId, _store.Load().Auth!.CloudAccounts[0].ProfileSync.Keys);
    }

    [Fact]
    public async Task RunSyncPass_deletes_local_when_cloud_was_deleted_on_another_machine()
    {
        SeedAccount("acct-1", "refresh-1");
        var defaultId = _profiles.GetActiveEntry()!.Id;
        var otherId = _profiles.CreateProfile("Other").Id;
        _profiles.SwitchProfile(defaultId);

        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(otherId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[otherId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });

        // Cloud list no longer carries "Other" (deleted from another machine).
        // "Default" has no sync record, so it decides Push, not DeleteLocal -
        // wired to fail silently so it does not interfere with the assertions.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _) => CloudApiResult<CloudPutProfileResult>.NetworkError("not wired");

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.DoesNotContain(_profiles.GetManifest().Profiles, p => p.Id == otherId);
        Assert.DoesNotContain(otherId, _store.Load().Auth!.CloudAccounts[0].ProfileSync.Keys);
        // Default (never synced, cloud-missing) is untouched by the delete path.
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == defaultId);
    }

    [Fact]
    public async Task RunSyncPass_repushes_instead_of_deleting_the_only_local_profile()
    {
        SeedAccount("acct-1", "refresh-1");
        var onlyId = _profiles.GetActiveEntry()!.Id;
        var hash = CloudProfileSyncService.HashPayload(_profiles.ExportProfileForSync(onlyId)!);
        _store.Update(s => s.Auth!.CloudAccounts[0].ProfileSync[onlyId] = new CloudProfileSyncRecord { Revision = 1, LastSyncedHash = hash });

        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _) => CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 2 });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Single(_profiles.GetManifest().Profiles);
        Assert.Equal(onlyId, _profiles.GetManifest().Profiles[0].Id);
        Assert.Equal(1, _api.PutProfileCalls);
        Assert.Equal(2, _store.Load().Auth!.CloudAccounts[0].ProfileSync[onlyId].Revision);
    }

    // ── account switch (archive + wholesale replace) ────────────────────

    [Fact]
    public async Task HandleSwitch_pushes_outgoing_pending_changes_archives_then_replaces_with_the_incoming_library()
    {
        SeedAccount("from-acct", "refresh-from");
        var oldDefaultId = _profiles.GetActiveEntry()!.Id;
        _profiles.CreateProfile("Work"); // 2 local-only profiles now under "from-acct", never synced.
        _profiles.SwitchProfile(oldDefaultId);

        // Seed the incoming ("to") account too so WithAuthAsync can mint a token for it.
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        var refreshTokenToAccount = new Dictionary<string, string> { ["refresh-from"] = "from-acct", ["refresh-to"] = "to-acct" };
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-for-" + refreshTokenToAccount[rt],
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = refreshTokenToAccount[rt], Email = "x@example.com", Username = "x", EmailVerified = true },
        });

        var pushedProfileIds = new List<string>();
        _api.OnPutProfile = (token, profileId, _) =>
        {
            Assert.Equal("access-for-from-acct", token); // the outgoing flush must authenticate as the OUTGOING account.
            pushedProfileIds.Add(profileId);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        var incomingSettings = new NexusSettings();
        incomingSettings.Lighting.GlobalBrightness = 0.42f;
        _api.OnListProfiles = token =>
        {
            Assert.Equal("access-for-to-acct", token);
            return CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
            {
                new() { ProfileId = "cloud-profile-1", Name = "Incoming", Revision = 5 },
            });
        };
        _api.OnGetProfile = (token, profileId) =>
        {
            Assert.Equal("access-for-to-acct", token);
            Assert.Equal("cloud-profile-1", profileId);
            return CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
            {
                ProfileId = profileId,
                Name = "Incoming",
                Revision = 5,
                Payload = new ProfileExport { Name = "Incoming", Settings = incomingSettings },
            });
        };

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        // Outgoing account: both local-only profiles were pushed before the swap.
        Assert.Equal(2, pushedProfileIds.Count);

        // Archive exists and holds the outgoing library.
        var archiveRoot = Path.Combine(_tempDir, "profiles-archive");
        Assert.True(Directory.Exists(archiveRoot));
        var archiveDirs = Directory.GetDirectories(archiveRoot);
        var archived = Assert.Single(archiveDirs);
        Assert.Equal(2, Directory.GetFiles(archived, "profile-*.json").Length);

        // Local library now mirrors the incoming account exactly.
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("cloud-profile-1", manifest.Profiles[0].Id);
        Assert.Equal("cloud-profile-1", manifest.ActiveProfileId);
        Assert.Equal(0.42f, _store.Load().Lighting.GlobalBrightness);

        var toRecord = _store.Load().Auth!.CloudAccounts.Single(a => a.AccountId == "to-acct");
        Assert.Equal(5, toRecord.ProfileSync["cloud-profile-1"].Revision);

        // A queued duplicate (the OnAccountActivated pass completed the switch
        // first via its redirect) must be a no-op: re-running would flush the
        // INCOMING library to the OUTGOING account's cloud.
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);
        Assert.Equal(2, pushedProfileIds.Count);
        Assert.Single(Directory.GetDirectories(archiveRoot));
    }

    [Fact]
    public async Task HandleSwitch_with_an_empty_incoming_cloud_library_falls_back_to_a_default_profile()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnPutProfile = (_, _, _) => CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("Default", manifest.Profiles[0].Name);
    }

    [Fact]
    public async Task HandleSwitch_aborts_without_touching_the_local_library_when_the_incoming_list_call_fails()
    {
        SeedAccount("from-acct", "refresh-from");
        var originalProfileId = _profiles.GetActiveEntry()!.Id;
        _profiles.CreateProfile("Work");
        _profiles.SwitchProfile(originalProfileId);
        var originalCount = _profiles.GetManifest().Profiles.Count;

        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        // The incoming account's profile list is unreachable - must NOT be
        // treated as "this account has zero cloud profiles".
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.NetworkError("dns failure");

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        Assert.Equal("offline", _sync.GetStatus().State);
        Assert.Equal(originalCount, _profiles.GetManifest().Profiles.Count);
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == originalProfileId);
        // No archive was created - ArchiveLibrary is only called after the
        // incoming library is fully and successfully retrieved.
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "profiles-archive")));
    }

    [Fact]
    public async Task HandleSwitch_aborts_without_touching_the_local_library_when_a_single_profile_fetch_fails()
    {
        SeedAccount("from-acct", "refresh-from");
        var originalCount = _profiles.GetManifest().Profiles.Count;

        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { ProfileId = "cloud-1", Name = "One", Revision = 1 },
            new() { ProfileId = "cloud-2", Name = "Two", Revision = 1 },
        });
        // First profile fetches fine, second fails - a partial pull must not
        // silently drop the second profile from the resulting library.
        _api.OnGetProfile = (_, profileId) => profileId == "cloud-1"
            ? CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
            {
                ProfileId = "cloud-1", Name = "One", Revision = 1,
                Payload = new ProfileExport { Name = "One", Settings = new NexusSettings() },
            })
            : CloudApiResult<CloudProfileDto>.NetworkError("timeout");

        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);

        Assert.Equal("offline", _sync.GetStatus().State);
        Assert.Equal(originalCount, _profiles.GetManifest().Profiles.Count);
    }

    [Fact]
    public async Task RunSyncPass_retries_a_stalled_switch_instead_of_an_incremental_pass()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        _store.Update(s => s.Auth!.ActiveCloudAccountId = "to-acct"); // the switch already flipped the active pointer
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.NetworkError("dns failure");
        var pushTokens = new List<string>();
        _api.OnPutProfile = (token, _, _) =>
        {
            pushTokens.Add(token);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };

        // First attempt fails and leaves a pending switch.
        _sync.AnnounceSwitch("from-acct", "to-acct");
        await _sync.HandleSwitchAsync("from-acct", "to-acct", CancellationToken.None);
        Assert.Equal("offline", _sync.GetStatus().State);

        // The list call now succeeds - a plain RunSyncPassAsync tick for the
        // (already active) "to-acct" must redo the wholesale switch, not an
        // incremental pass that would upload "from-acct"'s local library
        // under "to-acct"'s identity.
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        await _sync.RunSyncPassAsync("to-acct", CancellationToken.None);

        Assert.Equal("idle", _sync.GetStatus().State);
        // FlushAccountAsync legitimately pushes from-acct's never-synced local
        // profile as part of the switch's outgoing flush - the point being
        // tested is that every such push authenticates as from-acct, never as
        // to-acct (which would mean the redirect ran an incremental pass
        // against to-acct's identity instead of retrying the wholesale switch).
        Assert.All(pushTokens, t => Assert.Equal("tok-refresh-from", t));
        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("Default", manifest.Profiles[0].Name);
    }

    [Fact]
    public async Task RunSyncPass_preserves_dirty_tracking_across_a_failed_push_so_the_next_tick_retries()
    {
        SeedAccount("acct-1", "refresh-1");
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
        _api.OnPutProfile = (_, _, _) => CloudApiResult<CloudPutProfileResult>.NetworkError("dns failure");

        // Local-only profile, no sync record yet -> Push decision. The first
        // pass only SEEDS dirtySince (a fresh debounce window starts counting
        // from "first noticed dirty", not from any earlier clock state) - no
        // push is attempted yet.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(0, _api.PutProfileCalls);

        // Debounce window elapses - the push is attempted and fails.
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(1, _api.PutProfileCalls);

        // Same instant (no further clock advance) - a fix that reset the
        // dirty-since timestamp on a failed push would make this attempt wait
        // another full 60s; it must retry immediately instead.
        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        Assert.Equal(2, _api.PutProfileCalls);
    }

    [Fact]
    public async Task RunGuardedAsync_swallows_a_timeout_shaped_cancellation_instead_of_faulting_the_service_loop()
    {
        // Simulates HttpClient.Timeout: a TaskCanceledException whose own
        // CancellationToken is not the caller's ct, so ct.IsCancellationRequested
        // stays false. A filter that rethrows on any OperationCanceledException
        // regardless of ct would let this escape RunGuardedAsync and fault the
        // BackgroundService's execute task.
        Task Action() => throw new TaskCanceledException("simulated timeout", null, CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => _sync.RunGuardedAsync(Action, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunGuardedAsync_rethrows_when_the_callers_own_ct_was_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Never reached: the gate wait itself (_syncGate.WaitAsync(ct)) is the
        // first thing RunGuardedAsync does, and throws on the already-cancelled
        // token before action() is ever invoked - proving the propagation
        // happens at the earliest possible point, not just inside the action.
        Task Action() => throw new InvalidOperationException("should not run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sync.RunGuardedAsync(Action, cts.Token));
    }

    [Fact]
    public void AnnounceSwitch_sets_pending_fields_immediately()
    {
        Assert.Equal((null, null), _sync.PendingSwitch);

        _sync.AnnounceSwitch("from-acct", "to-acct");

        Assert.Equal(("from-acct", "to-acct"), _sync.PendingSwitch);
    }

    [Fact]
    public async Task Interleaved_sync_pass_between_switch_announce_and_switch_handle_redirects_instead_of_running_incrementally()
    {
        SeedAccount("from-acct", "refresh-from");
        _store.Update(s => s.Auth!.CloudAccounts.Add(new CloudAccountRecord { AccountId = "to-acct", RefreshToken = "refresh-to" }));
        // CloudAccountService flips ActiveCloudAccountId to the incoming
        // account before firing OnAccountSwitching - a racing RunSyncPassAsync
        // for "to-acct" would otherwise bail out immediately on its own
        // active-account guard, defeating the point of this test.
        _store.Update(s => s.Auth!.ActiveCloudAccountId = "to-acct");
        _api.OnRefresh = rt => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "tok-" + rt,
            RefreshToken = rt,
            Account = new CloudAccountDto { Id = rt == "refresh-from" ? "from-acct" : "to-acct", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        var pushTokens = new List<string>();
        _api.OnPutProfile = (token, _, _) =>
        {
            pushTokens.Add(token);
            return CloudApiResult<CloudPutProfileResult>.Ok(new CloudPutProfileResult { Revision = 1 });
        };
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());

        // AnnounceSwitch is exactly what the real OnAccountSwitching handler
        // runs synchronously before its Task.Run(HandleSwitchAsync) dispatch -
        // this is the earliest possible moment a second, racing
        // RunSyncPassAsync call (the sibling OnAccountActivated dispatch, or a
        // concurrent 15s tick) could interleave, before any background work
        // has run at all.
        _sync.AnnounceSwitch("from-acct", "to-acct");

        await _sync.RunSyncPassAsync("to-acct", CancellationToken.None);

        // Redirected into the wholesale switch (which pushes from-acct's
        // pending local profile as its outgoing flush) instead of an
        // incremental pass, which would read "no sync record for to-acct" and
        // upload the still-outgoing local library as new profiles under
        // to-acct's identity - the cross-account leakage this fix prevents.
        Assert.NotEmpty(pushTokens);
        Assert.All(pushTokens, t => Assert.Equal("tok-refresh-from", t));
        // The switch actually completed (not just redirected-and-stuck).
        Assert.Equal((null, null), _sync.PendingSwitch);
    }
}
