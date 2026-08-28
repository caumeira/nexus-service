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
    public async Task Import_creates_a_new_profile_named_after_the_source_machine()
    {
        var remote = new NexusSettings();
        remote.Lighting.GlobalBrightness = 0.25f;
        SeedRemote("install-y70", "HYTEY70", "p-remote", remote);
        var before = _profiles.GetManifest().Profiles.Count;

        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
        }, CancellationToken.None);

        Assert.True(result.Success);
        var manifest = _profiles.GetManifest().Profiles;
        Assert.Equal(before + 1, manifest.Count);
        var created = Assert.Single(manifest.Where(p => p.Name == "Their Default (HYTEY70)"));
        Assert.Equal(0.25f, _profiles.ExportProfile(created.Id)!.Lighting.GlobalBrightness);
    }

    [Fact]
    public async Task Import_leaves_every_existing_profile_untouched()
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
        }, CancellationToken.None);

        Assert.True(result.Success);
        // The copy lands beside the active profile rather than over it.
        var settings = _store.Load();
        Assert.Equal(1.0f, settings.Lighting.GlobalBrightness);
        Assert.Equal(1.0, settings.Cooling.GlobalSpeedModifier);
    }

    [Fact]
    public async Task Importing_the_same_profile_twice_reports_a_name_conflict()
    {
        SeedRemote("install-y70", "HYTEY70", "p-remote", new NexusSettings());
        var first = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
        }, CancellationToken.None);
        Assert.True(first.Success);

        var second = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
        }, CancellationToken.None);

        // ProfileNameConflictException does not derive from
        // InvalidOperationException, so this used to escape as a 500.
        Assert.False(second.Success);
        Assert.Equal("profile_name_taken", second.ErrorCode);
        Assert.Equal(409, second.StatusCode);
    }

    [Fact]
    public async Task Replacing_on_conflict_overwrites_in_place_without_adding_a_profile()
    {
        var remote = new NexusSettings();
        remote.Lighting.GlobalBrightness = 0.25f;
        SeedRemote("install-y70", "HYTEY70", "p-remote", remote);
        await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70", ProfileId = "p-remote",
        }, CancellationToken.None);
        var afterFirst = _profiles.GetManifest().Profiles.Count;

        remote.Lighting.GlobalBrightness = 0.75f;
        SeedRemote("install-y70", "HYTEY70", "p-remote", remote);
        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70", ProfileId = "p-remote", ReplaceExisting = true,
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(afterFirst, _profiles.GetManifest().Profiles.Count);
        var entry = Assert.Single(_profiles.GetManifest().Profiles.Where(p => p.Name == "Their Default (HYTEY70)"));
        Assert.Equal(0.75f, _profiles.ExportProfile(entry.Id)!.Lighting.GlobalBrightness);
    }

    [Fact]
    public async Task Import_reports_the_local_profile_cap()
    {
        SeedRemote("install-y70", "HYTEY70", "p-remote", new NexusSettings());
        while (_profiles.GetManifest().Profiles.Count < 5)
        {
            _profiles.CreateProfile("Filler " + _profiles.GetManifest().Profiles.Count);
        }

        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("profile_limit_reached", result.ErrorCode);
    }

    [Fact]
    public async Task Import_is_refused_when_signed_out()
    {
        _store.Update(s => s.Auth!.ActiveCloudAccountId = null);

        var result = await _library.ImportAsync(new CloudImportRequest
        {
            InstallId = "install-y70",
            ProfileId = "p-remote",
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
    }
}
