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
/// friends): the same push/pull/conflict/tombstone decision table as
/// profiles, run against the account-wide /account/app-data routes via a
/// FakeCloudApiClient. Real AppDataStore against a temp dir, matching
/// CloudProfileSyncServiceTests' style for the profile side.
/// </summary>
public sealed class CloudAppDataSyncTests : IDisposable
{
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
        SeedAccount("acct-1", "refresh-1");
        _appData.Put("com.test.app", "save", 0, Json("""{"a":1}"""));
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>());
        _api.OnPutAppData = (_, _, _, _) => CloudApiResult<CloudPutAppDataResult>.Ok(new CloudPutAppDataResult { Revision = 1, UpdatedAt = "t" });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(1, _api.PutAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.NotNull(stored!.Cloud);
        Assert.Equal(1, stored.Cloud!.Revision);
    }

    [Fact]
    public async Task A_cloud_only_document_with_no_local_copy_is_pulled_restore_after_reinstall()
    {
        SeedAccount("acct-1", "refresh-1");
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 4, UpdatedAt = "t", SizeBytes = 3 },
        });
        _api.OnGetAppData = (_, _, _) => CloudApiResult<CloudAppDataDto>.Ok(
            new CloudAppDataDto { Revision = 4, UpdatedAt = "t", Payload = Json("""{"fish":9}""") });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(1, _api.GetAppDataCalls);
        var stored = _appData.TryRead("com.test.app", "save");
        Assert.NotNull(stored);
        Assert.Equal(9, stored!.Data.GetProperty("fish").GetInt32());
        Assert.Equal(4, stored.Cloud!.Revision);
    }

    [Fact]
    public async Task Local_dirty_and_cloud_moved_is_a_conflict_that_pushes_or_pulls_nothing()
    {
        SeedAccount("acct-1", "refresh-1");
        var doc = _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "t",
        });
        // Local edit after the last sync.
        _appData.Put("com.test.app", "save", doc.Revision, Json("2"));

        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 2, UpdatedAt = "t2", UpdatedByInstallId = "other-machine" },
        });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(0, _api.GetAppDataCalls);
        var conflict = Assert.Single(_sync.AppDataConflicts);
        Assert.Equal("com.test.app", conflict.AppId);
        Assert.Equal("save", conflict.Key);
        Assert.Equal(2, conflict.CloudRevision);
        Assert.Equal("other-machine", conflict.CloudUpdatedByInstallId);
    }

    [Fact]
    public async Task Local_clean_and_cloud_unchanged_does_nothing()
    {
        SeedAccount("acct-1", "refresh-1");
        _appData.Put("com.test.app", "save", 0, Json("1"));
        _appData.SetCloudState("com.test.app", "save", new AppDataCloudState
        {
            Revision = 1,
            Hash = CloudProfileSyncService.HashAppData(Json("1")),
            SyncedAt = "t",
        });
        _api.OnListAppData = _ => CloudApiResult<List<CloudAppDataSummaryDto>>.Ok(new List<CloudAppDataSummaryDto>
        {
            new() { AppId = "com.test.app", Key = "save", Revision = 1, UpdatedAt = "t" },
        });

        await _sync.RunSyncPassAsync("acct-1", CancellationToken.None);

        Assert.Equal(0, _api.PutAppDataCalls);
        Assert.Equal(0, _api.GetAppDataCalls);
    }
}
