using System.Text.Json;
using Nexus.Service.Cloud;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>
/// CloudProfileSyncService's app-data extension (SyncAppDataAsync and
/// friends): push/pull/newest-wins/account-scoping against the account-wide
/// /account/app-data routes via a FakeCloudApiClient. Unlike profiles, app
/// data has no user-facing conflict picker - a genuine same-account two-device
/// divergence resolves by edit time; a doc that never belonged to the signed-in
/// account never mixes in, cloud-side. Real AppDataStore against a temp dir,
/// matching CloudProfileSyncServiceTests' style for the profile side.
/// </summary>
public sealed class CloudAppDataSyncTests : IDisposable
{
    private const string AccountId = "acct-1";
    private const string AppId = "com.test.app";
    private const string Key = "save";

    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeCloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly StubCoolingProvider _fans;
    private readonly AppDataStore _appData;
    private readonly CloudProfileSyncService _sync;
    private readonly ManualTimeProvider _clock;

    public CloudAppDataSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-appdata-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();

        _api = new FakeCloudApiClient();
        _fans = new StubCoolingProvider(_store);
        _appData = new AppDataStore(() => Path.Combine(_tempDir, "app-data"));
        _clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _accounts = new CloudAccountService(_api, _store, _clock);
        _sync = new CloudProfileSyncService(_api, _accounts, _profiles, _store, _fans, _appData, _clock);
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

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

    /// <summary>Builds the envelope payload the service itself sends as a cloud row's payload (see AppDataCloudEnvelope) - editedAt is the document's true edit time, independent of whatever the fake "server" stamps as its row-level updatedAt (upload time).</summary>
    private static JsonElement Envelope(string editedAt, JsonElement data)
    {
        using var doc = JsonDocument.Parse($$"""{"nexusAppData":1,"editedAt":{{JsonSerializer.Serialize(editedAt)}},"data":{{data.GetRawText()}}}""");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_local_only_document_with_no_cloud_row_is_adopted_by_this_account()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) => CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.NotNull(stored!.Cloud);
        Assert.Equal(AccountId, stored.Cloud!.AccountId);
        Assert.Equal(1, stored.Cloud.Revision);
    }

    [Fact]
    public async Task A_cloud_only_document_with_no_local_copy_is_pulled_restore_after_reinstall()
    {
        SeedAccount(AccountId, "refresh-1");
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 4, UpdatedAt = "t", SizeBytes = 3 },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 4, UpdatedAt = "t", Payload = Json("""{"fish":9}""") });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.NotNull(stored);
        Assert.Equal(9, stored!.Data.GetProperty("fish").GetInt32());
        Assert.Equal(AccountId, stored.Cloud!.AccountId);
        Assert.Equal(4, stored.Cloud.Revision);
    }

    // ── rule 2: account scoping ──────────────────────────────────────────

    [Fact]
    public async Task A_doc_synced_under_another_account_is_archived_then_replaced_by_this_accounts_cloud_copy()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":"someone-elses-save"}"""));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState { AccountId = "other-account", Revision = 1, Hash = "irrelevant", SyncedAt = "t" });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 7, UpdatedAt = "t" },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 7, UpdatedAt = "t", Payload = Json("""{"a":"this-accounts-progress"}""") });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls); // never pushed the foreign doc
        var live = _appData.TryRead(AppId, Key);
        Assert.Equal("this-accounts-progress", live!.Data.GetProperty("a").GetString());
        var archived = ReadArchivedDoc("other-account", AppId, Key);
        Assert.Equal("someone-elses-save", archived!.Data.GetProperty("a").GetString());
    }

    [Fact]
    public async Task A_doc_synced_under_another_account_with_no_row_for_this_account_is_archived_and_never_pushed()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":"someone-elses-save"}"""));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState { AccountId = "other-account", Revision = 1, Hash = "irrelevant", SyncedAt = "t" });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Null(_appData.TryRead(AppId, Key));
        var archived = ReadArchivedDoc("other-account", AppId, Key);
        Assert.Equal("someone-elses-save", archived!.Data.GetProperty("a").GetString());
    }

    [Fact]
    public async Task A_blank_local_only_doc_is_archived_and_replaced_when_the_account_already_has_progress()
    {
        SeedAccount(AccountId, "refresh-1");
        // Never synced anywhere - e.g. the blank default a fresh install/reinstall autosaves before sign-in.
        _appData.Put(AppId, Key, 0, Json("""{"eggs":0}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 12, UpdatedAt = "t" },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 12, UpdatedAt = "t", Payload = Json("""{"eggs":500}""") });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        var live = _appData.TryRead(AppId, Key);
        Assert.Equal(500, live!.Data.GetProperty("eggs").GetInt32());
        var archived = ReadArchivedDoc("local", AppId, Key);
        Assert.Equal(0, archived!.Data.GetProperty("eggs").GetInt32());
    }

    private AppDataFile? ReadArchivedDoc(string archiveNamespace, string appId, string key)
    {
        var path = Path.Combine(_tempDir, "app-data", ".archive", archiveNamespace, appId, key + ".json");
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize(File.ReadAllText(path), Nexus.Service.Serialization.PersistenceJsonContext.Default.AppDataFile);
    }

    // ── rule 3: a cloud row that disappeared ─────────────────────────────

    [Fact]
    public async Task A_clean_doc_whose_cloud_row_disappeared_is_deleted_locally()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState { AccountId = AccountId, Revision = 1, Hash = CloudProfileSyncService.HashAppData(Json("1")), SyncedAt = "t" });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Null(_appData.TryRead(AppId, Key));
        Assert.Equal(0, _api.PutAppDataCalls);
    }

    [Fact]
    public async Task A_dirty_doc_whose_cloud_row_disappeared_is_pushed_back_instead_of_deleted()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState { AccountId = AccountId, Revision = 1, Hash = CloudProfileSyncService.HashAppData(Json("1")), SyncedAt = "t" });
        _appData.Put(AppId, Key, doc.Revision, Json("2")); // unsynced edit after the row was recorded
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, body) =>
        {
            Assert.Equal(0, body.BaseRevision); // the row is gone server-side - recreate it
            return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" });
        };

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        Assert.NotNull(_appData.TryRead(AppId, Key));
    }

    // ── same-account, two-device divergence: edit time, not upload time ──

    [Fact]
    public async Task Both_sides_moved_and_local_is_newer_pushes_without_asking()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            EditedAt = "2026-01-01T00:00:00Z",
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put(AppId, Key, doc.Revision, Json("2")); // real local edit - UpdatedAt is now "now", newer than the cloud edit time below

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 2, UpdatedAt = "2999-01-01T00:00:00Z" /* upload time - irrelevant to the decision */, UpdatedByInstallId = "other-machine" },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 2,
            UpdatedAt = "2999-01-01T00:00:00Z",
            Payload = Envelope("2026-01-01T00:00:01Z" /* the OTHER machine's real edit time - older than ours */, Json("3")),
        });
        _api.OnPutAppData = (_, _, _, body) =>
        {
            Assert.Equal(2, body.BaseRevision); // rebased on the fetched current revision
            Assert.Equal(2, body.Payload.GetProperty("data").GetInt32());
            return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 3, UpdatedAt = "t" });
        };

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal(3, stored!.Cloud!.Revision);
    }

    [Fact]
    public async Task Both_sides_moved_and_cloud_is_newer_pulls_without_asking()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            EditedAt = "2026-01-01T00:00:00Z",
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put(AppId, Key, doc.Revision, Json("2"));

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 2, UpdatedAt = "2026-01-01T00:00:01Z", UpdatedByInstallId = "other-machine" },
        });
        // The other machine's real edit time is in the far future - newer
        // than our real "now" local edit above.
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 2,
            UpdatedAt = "2026-01-01T00:00:01Z",
            Payload = Envelope("2999-01-01T00:00:00Z", Json("""{"x":"cloud"}""")),
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal("cloud", stored!.Data.GetProperty("x").GetString());
        Assert.Equal(2, stored.Cloud!.Revision);
        Assert.Equal("2999-01-01T00:00:00Z", stored.Cloud.EditedAt);
    }

    [Fact]
    public async Task Both_sides_moved_to_the_same_content_records_metadata_without_pushing_or_pulling()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            EditedAt = "2026-01-01T00:00:00Z",
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put(AppId, Key, doc.Revision, Json("2")); // both machines independently landed on the same new value

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 2, UpdatedAt = "t" },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 2,
            UpdatedAt = "t",
            Payload = Envelope("2026-01-01T00:00:01Z", Json("2")),
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal(2, stored!.Cloud!.Revision);
    }

    // ── rule 1: a 409 is never success ────────────────────────────────────

    [Fact]
    public async Task A_409_on_push_re_reads_and_still_wins_retries_once_and_succeeds()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());

        var putCalls = 0;
        _api.OnPutAppData = (_, _, _, body) =>
        {
            putCalls++;
            if (putCalls == 1)
            {
                return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" }, statusCode: 409);
            }
            Assert.Equal(1, body.BaseRevision); // rebased on the row the 409 revealed
            return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 2, UpdatedAt = "t" });
        };
        // The row the 409 revealed is OLDER than our local edit - we still win.
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 1,
            UpdatedAt = "t",
            Payload = Envelope(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), Json("0")),
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(2, putCalls);
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal(2, stored!.Cloud!.Revision);
        Assert.Equal(1, stored.Data.GetProperty("a").GetInt32()); // our own edit, not the fetched row's
    }

    [Fact]
    public async Task A_409_on_push_where_the_cloud_is_now_newer_pulls_instead_of_retrying()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) =>
            CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" }, statusCode: 409);
        // The row the 409 revealed is NEWER than our local edit.
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 1,
            UpdatedAt = "t",
            Payload = Envelope(DateTimeOffset.UtcNow.AddDays(1).ToString("o"), Json("""{"a":99}""")),
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls); // never retried the push
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal(99, stored!.Data.GetProperty("a").GetInt32());
        Assert.Equal(1, stored.Cloud!.Revision);
    }

    [Fact]
    public async Task A_409_that_recurs_on_the_retry_leaves_the_doc_dirty_with_no_state_change()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) =>
            CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" }, statusCode: 409);
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto
        {
            Revision = 1,
            UpdatedAt = "t",
            Payload = Envelope(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), Json("0")), // older - we retry
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(2, _api.PutAppDataCalls); // one retry, both 409
        var stored = _appData.TryRead(AppId, Key);
        Assert.Null(stored!.Cloud); // never recorded as synced
    }

    [Fact]
    public async Task Local_clean_and_cloud_unchanged_does_nothing()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "t",
        });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 1, UpdatedAt = "t" },
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(0, _api.GetAppDataCalls);
    }

    // ── rule 12: nexus-api's doc cap surfaces as a permanent 400 ─────────

    [Fact]
    public async Task A_doc_cap_400_leaves_the_doc_dirty_without_retry_storms()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) =>
            CloudApiResult<CloudPutAppDataResult>.Fail(400, "app_data_limit_reached", "account is at its document cap");

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Null(_appData.TryRead(AppId, Key)!.Cloud);
    }

    // ── rule 6: cloud sync imports are CAS'd on the decision's local revision ─

    [Fact]
    public async Task A_local_write_racing_a_pull_wins_the_pull_is_skipped_and_retried_next_pass()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put(AppId, Key, 0, Json("1"));
        _appData.SetCloudState(AppId, Key, new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "t",
        });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = AppId, Key = Key, Revision = 2, UpdatedAt = "t" },
        });
        // The GET races a genuine local write that lands before the pull applies.
        _api.OnGetAppData = (_, _, _) =>
        {
            _appData.Put(AppId, Key, 1, Json("77"));
            return CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto { Revision = 2, UpdatedAt = "t", Payload = Json("99") });
        };

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        // The racing local write survives - the pull skipped rather than clobbering it.
        var stored = _appData.TryRead(AppId, Key);
        Assert.Equal(77, stored!.Data.GetInt32());
    }
}
