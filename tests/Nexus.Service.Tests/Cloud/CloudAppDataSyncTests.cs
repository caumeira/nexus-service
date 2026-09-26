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
/// friends): push/pull/newest-wins against the account-wide
/// /account/app-data routes via a FakeCloudApiClient. Unlike profiles, app
/// data has no user-facing conflict picker - a genuine two-sided divergence
/// resolves by newest updatedAt, ties keeping local. Real AppDataStore
/// against a temp dir, matching CloudProfileSyncServiceTests' style for the
/// profile side.
/// </summary>
public sealed class CloudAppDataSyncTests : IDisposable
{
    private const string AccountId = "acct-1";

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

    [Fact]
    public async Task A_local_only_document_with_no_cloud_row_is_pushed()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put("com.test.app", "save", 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) => CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
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
            new() { AppId = "com.test.app", Key = "save", Revision = 4, UpdatedAt = "t", SizeBytes = 3 },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 4, UpdatedAt = "t", Payload = Json("""{"fish":9}""") });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.NotNull(stored);
        Assert.Equal(9, stored!.Data.GetProperty("fish").GetInt32());
        Assert.Equal(AccountId, stored.Cloud!.AccountId);
        Assert.Equal(4, stored.Cloud.Revision);
    }

    /// <summary>Builds the envelope payload the service itself sends as a cloud row's payload (see AppDataCloudEnvelope) - editedAt is the document's true edit time, independent of whatever the fake "server" stamps as its row-level updatedAt (upload time).</summary>
    private static JsonElement Envelope(string editedAt, JsonElement data)
    {
        using var doc = JsonDocument.Parse($$"""{"nexusAppData":1,"editedAt":{{JsonSerializer.Serialize(editedAt)}},"data":{{data.GetRawText()}}}""");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Both_sides_moved_and_local_is_newer_pushes_without_asking()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            EditedAt = "2026-01-01T00:00:00Z",
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put("com.test.app", "save", doc.Revision, Json("2")); // real local edit - UpdatedAt is now "now", newer than the cloud edit time below

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 2, UpdatedAt = "2999-01-01T00:00:00Z" /* upload time - irrelevant to the decision */, UpdatedByInstallId = "other-machine" },
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
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.Equal(3, stored!.Cloud!.Revision);
    }

    [Fact]
    public async Task Both_sides_moved_and_cloud_is_newer_pulls_without_asking()
    {
        SeedAccount(AccountId, "refresh-1");
        var doc = _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            EditedAt = "2026-01-01T00:00:00Z",
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put("com.test.app", "save", doc.Revision, Json("2"));

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 2, UpdatedAt = "2026-01-01T00:00:01Z", UpdatedByInstallId = "other-machine" },
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
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.Equal("cloud", stored!.Data.GetProperty("x").GetString());
        Assert.Equal(2, stored.Cloud!.Revision);
        Assert.Equal("2999-01-01T00:00:00Z", stored.Cloud.EditedAt);
    }

    /// <summary>
    /// Coordinator repro: M1 edits eggs=9 at t1, M2 edits eggs=10 at t2 > t1.
    /// M1 syncs first - its push lands on the fake "server" at upload time t3,
    /// which this fake (like the real nexus-api) stamps as the row's
    /// updatedAt regardless of what the envelope's editedAt says, so t3 > t2.
    /// Comparing against t3 (the old bug) would make M2 think the cloud is
    /// newer and pull 9, discarding 10. Comparing against the envelope's
    /// editedAt (t1) instead means M2 correctly sees itself as newer, pushes
    /// 10, and a subsequent M1 sync pulls it - both machines end on 10.
    /// </summary>
    [Fact]
    public async Task Repro_two_machines_racing_pushes_the_later_edit_wins_not_the_later_upload()
    {
        var cloud = new Dictionary<string, (int Revision, JsonElement Payload, string UpdatedAt)>(StringComparer.Ordinal);
        var api = new FakeCloudApiClient();
        api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-acct-1",
            RefreshToken = "refresh",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
        api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(cloud
            .Select(kv => new CloudAppDataSummaryDto { AppId = kv.Key.Split('|')[0], Key = kv.Key.Split('|')[1], Revision = kv.Value.Revision, UpdatedAt = kv.Value.UpdatedAt })
            .ToList());
        api.OnGetAppData = (_, appId, key) => cloud.TryGetValue(appId + "|" + key, out var row)
            ? CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto { Revision = row.Revision, UpdatedAt = row.UpdatedAt, Payload = row.Payload })
            : CloudApiResult<CloudAppDataDto>.Ok(new CloudAppDataDto { Revision = 0 });
        api.OnPutAppData = (_, appId, key, body) =>
        {
            var k = appId + "|" + key;
            cloud.TryGetValue(k, out var row);
            if (body.BaseRevision != row.Revision)
            {
                return CloudApiResult<CloudPutAppDataResult>.Ok(
                    new CloudPutAppDataResult { Revision = row.Revision, UpdatedAt = row.UpdatedAt, UpdatedByInstallId = "someone" }, statusCode: 409);
            }
            // Stamped at upload time, exactly like the real nexus-api row -
            // this is what made the old (row.UpdatedAt-based) comparison wrong.
            var uploadedAt = DateTimeOffset.UtcNow.ToString("o");
            var newRevision = row.Revision + 1;
            cloud[k] = (newRevision, body.Payload, uploadedAt);
            return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = newRevision, UpdatedAt = uploadedAt });
        };

        (JsonConfigStore Store, ProfileManager Profiles, AppDataStore AppData, CloudProfileSyncService Sync) MakeMachine(string dir)
        {
            Directory.CreateDirectory(dir);
            var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var profiles = new ProfileManager(store);
            profiles.Initialize();
            store.Update(s =>
            {
                s.Auth ??= new AuthSettings();
                s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-" + dir });
                s.Auth.ActiveCloudAccountId = "acct-1";
            });
            var appData = new AppDataStore(() => Path.Combine(dir, "app-data"));
            var accounts = new CloudAccountService(api, store, _clock);
            var sync = new CloudProfileSyncService(api, accounts, profiles, store, new StubCoolingProvider(store), appData, _clock);
            return (store, profiles, appData, sync);
        }

        var m1Dir = Path.Combine(_tempDir, "m1");
        var m2Dir = Path.Combine(_tempDir, "m2");
        var m1 = MakeMachine(m1Dir);
        var m2 = MakeMachine(m2Dir);

        // M1 edits eggs=9 at t1 (older); M2 edits eggs=10 at t2 (newer than t1, older than the upload times below).
        WriteLocalDoc(m1Dir, "com.test.app", "save", Json("""{"eggs":9}"""), "2026-01-01T00:00:00Z");
        WriteLocalDoc(m2Dir, "com.test.app", "save", Json("""{"eggs":10}"""), "2026-01-01T00:00:05Z");

        await m1.Sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        await m2.Sync.RunSyncPassAsync("acct-1", CancellationToken.None);
        await m1.Sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(10, m1.AppData.TryRead("com.test.app", "save")!.Data.GetProperty("eggs").GetInt32());
        Assert.Equal(10, m2.AppData.TryRead("com.test.app", "save")!.Data.GetProperty("eggs").GetInt32());

        m1.Profiles.Dispose();
        m1.Store.Dispose();
        m2.Profiles.Dispose();
        m2.Store.Dispose();
    }

    private static void WriteLocalDoc(string root, string appId, string key, JsonElement data, string updatedAt)
    {
        var dir = Path.Combine(root, "app-data", appId);
        Directory.CreateDirectory(dir);
        var file = new AppDataFile { Revision = 1, UpdatedAt = updatedAt, Data = data };
        File.WriteAllText(Path.Combine(dir, key + ".json"),
            JsonSerializer.Serialize(file, Nexus.Service.Serialization.PersistenceJsonContext.Default.AppDataFile));
    }

    [Fact]
    public async Task A_document_synced_under_a_different_account_is_treated_as_never_synced()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            AccountId = "some-other-account",
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        // Cloud, under the NEW account, has no row yet for this doc - so
        // account-scoped bookkeeping must not report "clean" and skip.
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) => CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.Equal(AccountId, stored!.Cloud!.AccountId);
    }

    [Fact]
    public async Task Local_clean_and_cloud_unchanged_does_nothing()
    {
        SeedAccount(AccountId, "refresh-1");
        _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            AccountId = AccountId,
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "t",
        });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 1, UpdatedAt = "t" },
        });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(0, _api.GetAppDataCalls);
    }
}
