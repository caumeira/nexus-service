using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Profiles;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Profiles;

/// <summary>src/Profiles/ProfileArchivePackage.cs: the .nexusprofile zip round trip, and every entry it silently drops rather than failing the whole import.</summary>
public sealed class ProfileArchivePackageTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static string SampleProfileJson() => JsonSerializer.Serialize(
        new Nexus.Service.Models.Profiles.ProfileExport { Name = "Default", Settings = new NexusSettings() },
        PersistenceJsonContext.Default.ProfileExport);

    [Fact]
    public void WriteThenRead_round_trips_profile_json_and_app_data_entries_unchanged()
    {
        var profileJson = SampleProfileJson();
        var doc = new AppDataFile { Revision = 3, UpdatedAt = "2026-01-01T00:00:00Z", Data = Json("""{"fish":7}""") };

        var bytes = ProfileArchivePackage.Write(profileJson, new[] { ("com.hellonexus.aquarium", "save", doc) });

        using var ms = new MemoryStream(bytes);
        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Equal(profileJson, result.ProfileJson);
        var entry = Assert.Single(result.AppData);
        Assert.Equal("com.hellonexus.aquarium", entry.AppId);
        Assert.Equal("save", entry.Key);
        Assert.Equal(7, entry.Data.GetProperty("fish").GetInt32());
    }

    [Fact]
    public void Read_strips_cloud_sync_metadata_from_each_entry()
    {
        var doc = new AppDataFile
        {
            Revision = 1,
            UpdatedAt = "t",
            Data = Json("1"),
            Cloud = new AppDataCloudState { Revision = 1, Hash = "abc", SyncedAt = "t" },
        };
        var bytes = ProfileArchivePackage.Write(SampleProfileJson(), new[] { ("com.test.app", "save", doc) });

        using var ms = new MemoryStream(bytes);
        var result = ProfileArchivePackage.Read(ms);

        // The wire DTO the archive round-trips through (AppDataDocumentDto)
        // has no Cloud field at all - there is nothing for a reader to recover
        // even if it wanted to.
        var entry = Assert.Single(result.AppData);
        Assert.Equal(1, entry.Data.GetInt32());
    }

    [Fact]
    public void Read_fails_when_profile_json_is_missing()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("app-data/com.test.app/save.json").Open();
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Read_ignores_app_data_entries_with_an_invalid_app_id_or_key()
    {
        var profileJson = SampleProfileJson();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(profileJson);
                s.Write(bytes);
            }
            // Invalid app id (uppercase, no dot).
            using (var s = zip.CreateEntry("app-data/NOTVALID/save.json").Open())
            {
                JsonSerializer.Serialize(s, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = Json("1") },
                    AppJsonContext.Default.AppDataDocumentDto);
            }
            // Invalid key (uppercase).
            using (var s = zip.CreateEntry("app-data/com.test.app/BadKey.json").Open())
            {
                JsonSerializer.Serialize(s, new Nexus.Service.Models.Widgets.AppDataDocumentDto { Revision = 1, Data = Json("1") },
                    AppJsonContext.Default.AppDataDocumentDto);
            }
        }
        ms.Position = 0;

        var result = ProfileArchivePackage.Read(ms);

        Assert.True(result.Ok);
        Assert.Empty(result.AppData);
    }
}
