using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>
/// The cross-machine import path: list every machine on the account, show what
/// one of its profiles holds, and overwrite only the chosen categories of a
/// local profile from it. Real ProfileManager + JsonConfigStore against a temp
/// dir, FakeCloudApiClient standing in for api.hellonexus.com.
/// </summary>
public sealed class CloudProfileLibraryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;
    private readonly FakeCloudApiClient _api;
    private readonly CloudAccountService _accounts;
    private readonly CloudProfileLibraryService _library;

    public CloudProfileLibraryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-cloud-lib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();

        _api = new FakeCloudApiClient();
        _accounts = new CloudAccountService(_api, _store, TimeProvider.System);
        _library = new CloudProfileLibraryService(_api, _accounts, _profiles);

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.CloudAccounts.Add(new CloudAccountRecord { AccountId = "acct-1", RefreshToken = "refresh-1" });
            s.Auth.ActiveCloudAccountId = "acct-1";
        });
        _api.OnRefresh = _ => CloudApiResult<CloudAuthSession>.Ok(new CloudAuthSession
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            Account = new CloudAccountDto { Id = "acct-1", Email = "x@example.com", Username = "x", EmailVerified = true },
        });
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string OwnId => _accounts.ResolveStableInstallId();

    private void SeedRemote(string installId, string hostname, string profileId, NexusSettings payload)
    {
        _api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>
        {
            new() { InstallId = OwnId, Hostname = "THIS-MACHINE", LastSeenAt = "t0" },
            new() { InstallId = installId, Hostname = hostname, LastSeenAt = "t1" },
        });
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = installId, ProfileId = profileId, Name = "Their Default", Revision = 4, SizeBytes = 10, UpdatedAt = "t1" },
        });
        _api.OnGetProfile = (_, requestedInstall, requestedProfile) =>
            requestedInstall == installId && requestedProfile == profileId
                ? CloudApiResult<CloudProfileDto>.Ok(new CloudProfileDto
                {
                    InstallId = installId,
                    ProfileId = profileId,
                    Name = "Their Default",
                    Revision = 4,
                    UpdatedAt = "t1",
                    Payload = new ProfileExport { Name = "Their Default", Settings = payload },
                })
                : CloudApiResult<CloudProfileDto>.Fail(404, "not_found", "no such profile");
    }

    [Fact]
    public async Task Library_names_machines_by_hostname_and_flags_this_one()
    {
        SeedRemote("install-y70", "HYTEY70", "p-remote", new NexusSettings());

        var result = await _library.GetLibraryAsync(CancellationToken.None);

        Assert.True(result.Success);
        var machines = result.Value!.Machines;
        Assert.Equal(2, machines.Count);
        var mine = Assert.Single(machines.Where(m => m.IsThisMachine));
        Assert.Equal("THIS-MACHINE", mine.Hostname);
        var theirs = Assert.Single(machines.Where(m => !m.IsThisMachine));
        Assert.Equal("HYTEY70", theirs.Hostname);
        Assert.Equal("p-remote", Assert.Single(theirs.Profiles).ProfileId);
    }

    [Fact]
    public async Task Library_still_lists_a_profile_whose_machine_never_registered()
    {
        _api.OnListDevices = _ => CloudApiResult<List<CloudDeviceDto>>.Ok(new List<CloudDeviceDto>());
        _api.OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>
        {
            new() { InstallId = "legacy", ProfileId = "p-old", Name = "Old", Revision = 1 },
        });

        var result = await _library.GetLibraryAsync(CancellationToken.None);

        var machine = Assert.Single(result.Value!.Machines);
        Assert.Equal("legacy", machine.InstallId);
        Assert.Equal("", machine.Hostname);
        Assert.Equal("p-old", Assert.Single(machine.Profiles).ProfileId);
    }

    [Fact]
    public async Task Preview_reports_per_category_contents()
    {
        var remote = new NexusSettings();
        remote.Cooling.FanNames["fan-1"] = "Front intake";
        remote.Cooling.FanNames["fan-2"] = "Rear exhaust";
        remote.Keeb.RotaryLeft = "volume-down";
        SeedRemote("install-y70", "HYTEY70", "p-remote", remote);

        var result = await _library.GetPreviewAsync("install-y70", "p-remote", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("HYTEY70", result.Value!.Hostname);
        Assert.Equal(ProfileSharing.All.Count, result.Value.Categories.Count);
        var cooling = result.Value.Categories.Single(c => c.Category == ProfileSharing.Cooling);
        Assert.Equal(2, cooling.Metrics["namedFans"]);
        Assert.True(cooling.SizeBytes > 0);

        // Size is what the category ADDS. NexusSettings serializes every
        // property, so a category holding nothing must not report the shared
        // skeleton (~8KB on the real settings shape) as its own content.
        var theme = result.Value.Categories.Single(c => c.Category == ProfileSharing.Theme);
        Assert.Equal(0, theme.SizeBytes);
    }

    [Fact]
    public async Task Import_overwrites_only_the_chosen_categories()
    {
        var remote = new NexusSettings();
        remote.Lighting.GlobalBrightness = 0.25f;
        remote.Cooling.GlobalSpeedModifier = 2.5;
        SeedRemote("install-y70", "HYTEY70", "p-remote", remote);

        _store.Update(s =>
        {
            s.Lighting.GlobalBrightness = 1.0f;
            s.Cooling.GlobalSpeedModifier = 1.0;
        });
        _store.FlushNow();

        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
            Categories = new List<string> { ProfileSharing.Lighting },
        }, CancellationToken.None);

        Assert.True(result.Success);
        var settings = _store.Load();
        Assert.Equal(0.25f, settings.Lighting.GlobalBrightness);
        // Cooling was not selected, so the local value must survive untouched.
        Assert.Equal(1.0, settings.Cooling.GlobalSpeedModifier);
    }

    [Fact]
    public async Task Import_rejects_an_empty_or_unknown_category_set()
    {
        SeedRemote("install-y70", "HYTEY70", "p-remote", new NexusSettings());

        var empty = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
            Categories = new List<string>(),
        }, CancellationToken.None);
        Assert.False(empty.Success);
        Assert.Equal("no_categories", empty.ErrorCode);

        var bogus = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
            Categories = new List<string> { "not-a-category" },
        }, CancellationToken.None);
        Assert.False(bogus.Success);
        Assert.Equal("no_categories", bogus.ErrorCode);
    }

    [Fact]
    public async Task Import_is_refused_when_signed_out()
    {
        _store.Update(s => s.Auth!.ActiveCloudAccountId = null);

        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
            Categories = new List<string> { ProfileSharing.Lighting },
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
    }
}
