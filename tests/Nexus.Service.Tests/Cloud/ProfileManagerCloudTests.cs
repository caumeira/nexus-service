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
    public void ExportProfileForSync_carries_no_credentials_or_machine_scoped_state()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s =>
        {
            s.Auth = new AuthSettings { Token = "super-secret-desktop-token" };
            s.PrimaryProfileId = activeId;
            s.SharedCategories = new List<string> { "lighting" };
            s.HostDisplayName = "Y70-BOX";
            s.Steam.ApiKey = "steam-secret";
            s.Discord.ClientSecret = "discord-secret";
            s.HomeAssistant.Token = "hass-secret";
            s.Obs.Password = "obs-secret";
            s.Telemetry.InstallId = "this-machines-install-id";
        });
        _store.FlushNow();

        var export = _profiles.ExportProfileForSync(activeId);

        Assert.NotNull(export);
        Assert.NotNull(export!.Settings);
        var settings = export.Settings!;

        // The export is built from the shareable categories only, so every
        // credential block - all of which live at the NexusSettings root -
        // is left at its default rather than copied. Asserting on the token
        // values, not on the block being null, is what actually proves no
        // secret leaves the machine.
        Assert.Equal("", settings.Auth?.Token ?? "");
        Assert.Equal("", settings.Steam.ApiKey);
        Assert.Equal("", settings.Discord.ClientSecret);
        Assert.Equal("", settings.HomeAssistant.Token);
        Assert.Equal("", settings.Obs.Password);
        Assert.Equal("", settings.AiIntegration.Token);

        // Machine-scoped state must not travel either: it is never applied on
        // the receiving side, and it made two machines permanently differ.
        Assert.Equal("", settings.HostDisplayName);
        Assert.Equal("", settings.Telemetry.InstallId);
        Assert.Null(settings.PrimaryProfileId);
        // SharedCategories is not copied, so it holds the fresh-install
        // default rather than the source machine's ["lighting"].
        Assert.DoesNotContain("lighting", settings.SharedCategories);
        Assert.Equal(new NexusSettings().SharedCategories, settings.SharedCategories);
    }

    [Fact]
    public void ExportProfileForSync_keeps_the_shareable_categories()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s =>
        {
            s.Lighting.GlobalBrightness = 0.42f;
            s.Cooling.GlobalSpeedModifier = 1.75;
            s.Keeb.RotaryLeft = "custom-left";
        });
        _store.FlushNow();

        var settings = _profiles.ExportProfileForSync(activeId)!.Settings!;

        Assert.Equal(0.42f, settings.Lighting.GlobalBrightness);
        Assert.Equal(1.75, settings.Cooling.GlobalSpeedModifier);
        Assert.Equal("custom-left", settings.Keeb.RotaryLeft);
    }

    [Fact]
    public void ExportProfileForSync_strips_ai_integration_token()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.AiIntegration = new AiIntegrationSettings { Enabled = true, Token = "super-secret-mcp-token" });
        _store.FlushNow();

        var export = _profiles.ExportProfileForSync(activeId);

        Assert.NotNull(export);
        Assert.NotNull(export!.Settings);
        Assert.False(export.Settings!.AiIntegration.Enabled);
        Assert.Equal("", export.Settings.AiIntegration.Token);
    }

    [Fact]
    public void ExportProfileJson_strips_ai_integration_token()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.AiIntegration = new AiIntegrationSettings { Enabled = true, Token = "super-secret-mcp-token" });
        _store.FlushNow();

        var json = _profiles.ExportProfileJson(activeId);

        Assert.NotNull(json);
        Assert.DoesNotContain("super-secret-mcp-token", json);
    }

    [Fact]
    public void ExportProfileForSync_of_a_non_active_profile_also_strips_ai_integration_token()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        var other = _profiles.CreateProfile("Other");
        _profiles.SwitchProfile(activeId); // back to Default, "Other" is now the non-active profile
        _store.Update(s => s.AiIntegration = new AiIntegrationSettings { Enabled = true, Token = "super-secret-mcp-token" });
        _store.FlushNow();

        var export = _profiles.ExportProfileForSync(other.Id);

        Assert.NotNull(export);
        Assert.NotNull(export!.Settings);
        Assert.False(export.Settings!.AiIntegration.Enabled);
        Assert.Equal("", export.Settings.AiIntegration.Token);
    }

    [Fact]
    public void ImportProfileWithId_reactivating_leaves_live_ai_integration_untouched()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.AiIntegration = new AiIntegrationSettings { Enabled = true, Token = "live-mcp-token" });
        _store.FlushNow();

        var pulled = new NexusSettings();
        pulled.AiIntegration = new AiIntegrationSettings { Enabled = true, Token = "foreign-mcp-token" };
        _profiles.ImportProfileWithId(activeId, "Default", pulled);

        Assert.Equal("live-mcp-token", _store.Load().AiIntegration.Token);
    }

    [Fact]
    public void ExportProfileForSync_excludes_onboarding_completed()
    {
        var activeId = _profiles.GetActiveEntry()!.Id;
        _store.Update(s => s.OnboardingCompleted = true);
        _store.FlushNow();

        var export = _profiles.ExportProfileForSync(activeId);

        Assert.NotNull(export);
        Assert.NotNull(export!.Settings);
        Assert.False(export.Settings!.OnboardingCompleted);
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

    [Fact]
    public void ImportProfileWithId_auto_suffixes_when_colliding_with_a_different_local_profile()
    {
        _profiles.CreateProfile("Gaming");

        var entry = _profiles.ImportProfileWithId("cloud-1", "Gaming", new NexusSettings());

        Assert.Equal("Gaming (2)", entry.Name);
    }

    [Fact]
    public void ImportProfileWithId_picks_the_first_free_suffix_number()
    {
        _profiles.CreateProfile("Gaming");
        _profiles.CreateProfile("Gaming (2)");

        var entry = _profiles.ImportProfileWithId("cloud-1", "Gaming", new NexusSettings());

        Assert.Equal("Gaming (3)", entry.Name);
    }

    [Fact]
    public void ReplaceLibrary_dedupes_duplicate_names_within_the_incoming_set_by_ascending_id()
    {
        // cloud-a sorts before cloud-b (ordinal), so it keeps the plain name
        // even though it is second in the input list.
        _profiles.ReplaceLibrary(new List<(string, string, NexusSettings)>
        {
            ("cloud-b", "Default", new NexusSettings()),
            ("cloud-a", "Default", new NexusSettings()),
        });

        var manifest = _profiles.GetManifest();
        Assert.Equal("Default", manifest.Profiles.Single(p => p.Id == "cloud-a").Name);
        Assert.Equal("Default (2)", manifest.Profiles.Single(p => p.Id == "cloud-b").Name);
    }

    [Fact]
    public void ReplaceLibrary_within_set_dedupe_picks_the_first_free_suffix_number()
    {
        _profiles.ReplaceLibrary(new List<(string, string, NexusSettings)>
        {
            ("cloud-a", "Default", new NexusSettings()),
            ("cloud-b", "Default (2)", new NexusSettings()),
            ("cloud-c", "Default", new NexusSettings()),
        });

        var manifest = _profiles.GetManifest();
        Assert.Equal("Default", manifest.Profiles.Single(p => p.Id == "cloud-a").Name);
        Assert.Equal("Default (2)", manifest.Profiles.Single(p => p.Id == "cloud-b").Name);
        Assert.Equal("Default (3)", manifest.Profiles.Single(p => p.Id == "cloud-c").Name);
    }

    [Fact]
    public void Importing_a_colliding_name_increments_the_counter_instead_of_appending()
    {
        // The old behaviour appended blindly, so a name round-tripping between
        // two machines grew "Default" -> "Default (2)" -> "Default (2) (2)".
        // Initialize() already seeded a profile called "Default".
        var second = _profiles.ImportProfileWithId("id-a", "Default", new NexusSettings());
        Assert.Equal("Default (2)", second.Name);

        // The name that comes back from a machine that renamed it once. The
        // counter must advance rather than a second suffix being welded on.
        var third = _profiles.ImportProfileWithId("id-b", "Default (2)", new NexusSettings());
        Assert.Equal("Default (3)", third.Name);

        var fourth = _profiles.ImportProfileWithId("id-c", "Default (3)", new NexusSettings());
        Assert.Equal("Default (4)", fourth.Name);
        foreach (var entry in _profiles.GetManifest().Profiles)
        {
            Assert.DoesNotMatch(@"\(\d+\) \(\d+\)", entry.Name);
        }
    }
}
