using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>Covers the ProfileManager surface CloudProfileSyncService relies on: export stripping, id-preserving import, archive, and wholesale library replace. Real JsonConfigStore + ProfileManager against a temp directory, matching Integration/ProfileSwitchTests.cs.</summary>
public sealed class ProfileManagerCloudTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileManagerCloudTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-cloud-profile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ExportProfileForSync_strips_auth_and_machine_scoped_fields()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s =>
        {
            s.Auth = new AuthSettings { Token = "super-secret-desktop-token" };
            s.PrimaryProfileId = activeId;
            s.SharedCategories = new List<string> { "lighting" };
            s.HostDisplayName = "Y70-BOX";
        });
        _store.FlushNow();

        var export = _profiles.ExportProfileForSync(activeId);

        Assert.NotNull(export);
        Assert.NotNull(export!.Settings);
        Assert.Null(export.Settings!.Auth);
        Assert.Null(export.Settings.PrimaryProfileId);
        Assert.Empty(export.Settings.SharedCategories);
    }

    [Fact]
    public void ExportProfileForSync_unknown_profile_returns_null()
    {
        Assert.Null(_profiles.ExportProfileForSync("does-not-exist"));
    }

    [Fact]
    public void ImportProfileWithId_creates_a_new_profile_preserving_the_cloud_id()
    {
        var data = new NexusSettings();
        data.Lighting.GlobalBrightness = 42;

        var entry = _profiles.ImportProfileWithId("cloud-profile-xyz", "Gaming", data);

        Assert.Equal("cloud-profile-xyz", entry.Id);
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == "cloud-profile-xyz" && p.Name == "Gaming");
    }

    [Fact]
    public void ImportProfileWithId_overwrites_an_existing_non_active_profile_without_touching_active_settings()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        var other = _profiles.CreateProfile("Other");
        _profiles.SwitchProfile(activeId); // back to Default, "Other" is now the non-active profile

        _store.Update(s => s.Lighting.GlobalBrightness = 77);
        _store.FlushNow();
        var activeBrightnessBefore = _store.Load().Lighting.GlobalBrightness;

        var pulled = new NexusSettings();
        pulled.Lighting.GlobalBrightness = 11;
        _profiles.ImportProfileWithId(other.Id, "Other (cloud)", pulled);

        // The non-active profile's file changed, but the active in-memory
        // settings must be untouched.
        Assert.Equal(activeBrightnessBefore, _store.Load().Lighting.GlobalBrightness);
        Assert.Contains(_profiles.GetManifest().Profiles, p => p.Id == other.Id && p.Name == "Other (cloud)");

        _profiles.SwitchProfile(other.Id);
        Assert.Equal(11f, _store.Load().Lighting.GlobalBrightness);
    }

    [Fact]
    public void ImportProfileWithId_overwriting_the_active_profile_reloads_in_memory_settings()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        var switched = false;
        _profiles.OnProfileSwitched += () => switched = true;

        var pulled = new NexusSettings();
        pulled.Lighting.GlobalBrightness = 55;
        _profiles.ImportProfileWithId(activeId, "Default", pulled);

        Assert.Equal(55f, _store.Load().Lighting.GlobalBrightness);
        Assert.True(switched, "overwriting the active profile must re-baseline engines the same way a switch does");
    }

    [Fact]
    public void ImportProfileWithId_throws_when_creating_would_exceed_the_cap()
    {
        for (var i = 0; i < ProfileManager.MaxProfiles - 1; i++)
        {
            _profiles.CreateProfile($"Extra {i}");
        }
        Assert.Equal(ProfileManager.MaxProfiles, _profiles.GetManifest().Profiles.Count);

        Assert.Throws<InvalidOperationException>(() =>
            _profiles.ImportProfileWithId("one-too-many", "Nope", new NexusSettings()));
    }

    [Fact]
    public void ArchiveLibrary_copies_profiles_json_and_every_profile_file_without_touching_the_live_library()
    {
        _profiles.CreateProfile("Second");
        var archiveDir = _profiles.ArchiveLibrary();

        Assert.True(Directory.Exists(archiveDir));
        Assert.True(File.Exists(Path.Combine(archiveDir, "profiles.json")));
        Assert.Equal(2, Directory.GetFiles(archiveDir, "profile-*.json").Length);

        // Live library is untouched.
        Assert.Equal(2, _profiles.GetManifest().Profiles.Count);
    }

    [Fact]
    public void ReplaceLibrary_swaps_in_the_given_profiles_and_resets_primary_and_shared_categories()
    {
        var oldActive = _profiles.GetActiveEntry()!.Id;
        _profiles.SetPrimary(oldActive);
        _store.Update(s => s.SharedCategories = new List<string> { "lighting" });

        var incoming = new NexusSettings();
        incoming.Lighting.GlobalBrightness = 99;
        _profiles.ReplaceLibrary(new List<(string, string, NexusSettings)>
        {
            ("cloud-1", "From Cloud", incoming),
        });

        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("cloud-1", manifest.Profiles[0].Id);
        Assert.Equal("cloud-1", manifest.ActiveProfileId);
        Assert.Equal(99f, _store.Load().Lighting.GlobalBrightness);
        Assert.Equal("cloud-1", _store.Load().PrimaryProfileId);
        Assert.Empty(_store.Load().SharedCategories);

        // The old profile's file is gone.
        Assert.DoesNotContain(manifest.Profiles, p => p.Id == oldActive);
    }

    [Fact]
    public void ReplaceLibrary_with_an_empty_set_falls_back_to_a_fresh_default_profile()
    {
        _profiles.ReplaceLibrary(new List<(string, string, NexusSettings)>());

        var manifest = _profiles.GetManifest();
        Assert.Single(manifest.Profiles);
        Assert.Equal("Default", manifest.Profiles[0].Name);
        Assert.Equal(manifest.Profiles[0].Id, manifest.ActiveProfileId);
    }
}
