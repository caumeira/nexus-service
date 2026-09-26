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
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        _appData.Put("com.test.app", "save", doc.Revision, Json("2")); // local edit, updatedAt now newer than the cloud row below

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 2, UpdatedAt = "2026-01-01T00:00:01Z", UpdatedByInstallId = "other-machine" },
        });
        _api.OnPutAppData = (_, _, _, body) =>
        {
            Assert.Equal(2, body.BaseRevision); // rebased on the cloud row's current revision
            return CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 3, UpdatedAt = "t" });
        };

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        Assert.Equal(0, _api.GetAppDataCalls);
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
            SyncedAt = "2026-01-01T00:00:00Z",
        });
        // Local edit stamped as if it happened before the (newer) cloud edit -
        // Put always uses UtcNow, so force the local doc's UpdatedAt back in
        // time directly via a second Import (Import preserves the caller's
        // clock only indirectly, so assert via the cloud row instead: give the
        // cloud row a timestamp far in the future).
        _appData.Put("com.test.app", "save", doc.Revision, Json("2"));

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 2, UpdatedAt = "2999-01-01T00:00:00Z", UpdatedByInstallId = "other-machine" },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 2, UpdatedAt = "2999-01-01T00:00:00Z", Payload = Json("""{"x":"cloud"}""") });

        await _sync.RunSyncPassAsync(AccountId, CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.Equal("cloud", stored!.Data.GetProperty("x").GetString());
        Assert.Equal(2, stored.Cloud!.Revision);
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
