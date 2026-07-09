using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// CreateProfile/RenameProfile/ImportProfile collision guard: a trimmed,
/// case-insensitive name match against a DIFFERENT profile id throws
/// <see cref="ProfileNameConflictException"/>. Renaming a profile to its own
/// current name (any casing) is a success no-op, not a conflict.
/// </summary>
public sealed class ProfileNameUniquenessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileNameUniquenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-profile-names-" + Guid.NewGuid().ToString("N")[..8]);
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
    public void CreateProfile_throws_on_case_insensitive_trimmed_collision()
    {
        _profiles.CreateProfile("Gaming");

        Assert.Throws<ProfileNameConflictException>(() => _profiles.CreateProfile("  GAMING  "));
    }

    [Fact]
    public void CreateProfile_succeeds_with_a_distinct_name()
    {
        _profiles.CreateProfile("Gaming");

        var entry = _profiles.CreateProfile("Quiet");

        Assert.Equal("Quiet", entry.Name);
    }

    [Fact]
    public void RenameProfile_throws_when_colliding_with_a_different_profile()
    {
        _profiles.CreateProfile("Gaming");
        var second = _profiles.CreateProfile("Quiet");

        Assert.Throws<ProfileNameConflictException>(() => _profiles.RenameProfile(second.Id, "  gaming  "));
    }

    [Fact]
    public void RenameProfile_to_its_own_current_name_is_a_success_no_op()
    {
        var entry = _profiles.CreateProfile("Gaming");

        _profiles.RenameProfile(entry.Id, "GAMING");

        Assert.Equal("GAMING", _profiles.GetManifest().Profiles.Single(p => p.Id == entry.Id).Name);
    }

    [Fact]
    public void ImportProfile_throws_on_collision_instead_of_auto_suffixing()
    {
        _profiles.CreateProfile("Gaming");

        Assert.Throws<ProfileNameConflictException>(() =>
            _profiles.ImportProfile(" Gaming ", new NexusSettings()));
    }

    [Fact]
    public void ImportProfile_succeeds_with_a_distinct_name()
    {
        _profiles.CreateProfile("Gaming");

        var entry = _profiles.ImportProfile("Quiet", new NexusSettings());

        Assert.Equal("Quiet", entry.Name);
    }

    [Fact]
    public void ImportProfile_with_replaceExisting_overwrites_the_colliding_profile_in_place()
    {
        var created = _profiles.CreateProfile("Gaming");
        var countBefore = _profiles.GetManifest().Profiles.Count;

        var imported = new NexusSettings();
        imported.Cooling.PreferredGpuId = "replaced-marker";

        var entry = _profiles.ImportProfile("gaming", imported, replaceExisting: true);

        Assert.Equal(created.Id, entry.Id);
        Assert.Equal("gaming", entry.Name);
        Assert.Equal(countBefore, _profiles.GetManifest().Profiles.Count);
        Assert.True(DateTimeOffset.Parse(entry.UpdatedAt) >= DateTimeOffset.Parse(created.UpdatedAt));

        var exported = _profiles.ExportProfile(created.Id);
        Assert.Equal("replaced-marker", exported!.Cooling.PreferredGpuId);
    }

    [Fact]
    public void ImportProfile_with_replaceExisting_works_at_MaxProfiles_cap()
    {
        _profiles.CreateProfile("P1");
        _profiles.CreateProfile("P2");
        _profiles.CreateProfile("P3");
        var target = _profiles.CreateProfile("P4");
        Assert.Equal(ProfileManager.MaxProfiles, _profiles.GetManifest().Profiles.Count);

        var entry = _profiles.ImportProfile("p4", new NexusSettings(), replaceExisting: true);

        Assert.Equal(target.Id, entry.Id);
        Assert.Equal(ProfileManager.MaxProfiles, _profiles.GetManifest().Profiles.Count);
    }

    [Fact]
    public void ImportProfile_with_replaceExisting_overwrites_a_non_active_profile_without_switching()
    {
        var target = _profiles.CreateProfile("Gaming");
        var active = _profiles.CreateProfile("Quiet");
        Assert.Equal(active.Id, _profiles.GetManifest().ActiveProfileId);
        var countBefore = _profiles.GetManifest().Profiles.Count;

        var imported = new NexusSettings();
        imported.Cooling.PreferredGpuId = "nonactive-marker";

        var entry = _profiles.ImportProfile("gaming", imported, replaceExisting: true);

        Assert.Equal(target.Id, entry.Id);
        Assert.Equal(countBefore, _profiles.GetManifest().Profiles.Count);
        Assert.Equal(active.Id, _profiles.GetManifest().ActiveProfileId);

        var exported = _profiles.ExportProfile(target.Id);
        Assert.Equal("nonactive-marker", exported!.Cooling.PreferredGpuId);
    }
}
