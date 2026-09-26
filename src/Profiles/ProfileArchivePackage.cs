using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Profiles;

/// <summary>
/// Reader/writer for the .nexusprofile archive format: <c>profile.json</c>
/// (today's ProfileExport, unchanged) plus one
/// <c>app-data/&lt;appId&gt;/&lt;key&gt;.json</c> entry per app-data document,
/// stripped of its cloud sync metadata. Same ZipArchive shape as
/// <see cref="Nexus.Service.Deck.DeckPresetPackage"/>.
/// </summary>
public static class ProfileArchivePackage
{
    /// <summary>Generous relative to a single document's 256 KiB cap - bounds the whole archive against a hostile zip bomb, not normal use.</summary>
    public const long MaxArchiveBytes = 64 * 1024 * 1024;

    private const string ProfileJsonName = "profile.json";
    private const string AppDataPrefix = "app-data/";

    public sealed class Entry
    {
        public required string AppId { get; init; }
        public required string Key { get; init; }
        public required JsonElement Data { get; init; }
    }

    public sealed class ReadResult
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        /// <summary>Raw profile.json text, fed straight into ProfileManager.ImportProfileJson so the existing shader-param sanitisation and legacy-shape handling run unchanged.</summary>
        public string? ProfileJson { get; init; }
        public IReadOnlyList<Entry> AppData { get; init; } = new List<Entry>();

        public static ReadResult Fail(string error) => new() { Error = error };
    }

    public static byte[] Write(string profileJson, IEnumerable<(string AppId, string Key, AppDataFile Doc)> appData)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var profileStream = zip.CreateEntry(ProfileJsonName).Open())
            {
                var bytes = Encoding.UTF8.GetBytes(profileJson);
                profileStream.Write(bytes);
            }

            foreach (var (appId, key, doc) in appData)
            {
                var dto = new AppDataDocumentDto { Revision = doc.Revision, UpdatedAt = doc.UpdatedAt, Data = doc.Data };
                using var entryStream = zip.CreateEntry($"{AppDataPrefix}{appId}/{key}.json").Open();
                JsonSerializer.Serialize(entryStream, dto, AppJsonContext.Default.AppDataDocumentDto);
            }
        }
        return ms.ToArray();
    }

    public static ReadResult Read(Stream zipStream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return ReadResult.Fail("not a valid archive");
        }
        using (zip)
        {
            var totalBytes = zip.Entries.Sum(e => e.Length);
            if (totalBytes > MaxArchiveBytes)
            {
                return ReadResult.Fail("archive exceeds the 64 MB size limit");
            }

            var profileEntry = zip.GetEntry(ProfileJsonName);
            if (profileEntry is null)
            {
                return ReadResult.Fail("archive is missing profile.json");
            }

            string profileJson;
            using (var s = profileEntry.Open())
            using (var reader = new StreamReader(s, Encoding.UTF8))
            {
                profileJson = reader.ReadToEnd();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(profileJson, PersistenceJsonContext.Default.ProfileExport);
                if (parsed?.Settings is null)
                {
                    return ReadResult.Fail("profile.json is missing settings");
                }
            }
            catch (JsonException)
            {
                return ReadResult.Fail("profile.json is not valid JSON");
            }

            var entries = new List<Entry>();
            foreach (var zipEntry in zip.Entries)
            {
                if (!zipEntry.FullName.StartsWith(AppDataPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var rel = zipEntry.FullName[AppDataPrefix.Length..];
                var slash = rel.IndexOf('/');
                if (slash < 0 || !rel.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }
                var appId = rel[..slash];
                var key = rel[(slash + 1)..^".json".Length];
                // Invalid app ids / keys are ignored rather than failing the
                // whole import - an archive built by a future version could
                // carry app-data this version does not recognise.
                if (!AppIds.IsValid(appId) || !AppDataKeys.IsValid(key))
                {
                    continue;
                }
                // Per-entry envelope (revision/updatedAt JSON overhead) on top
                // of the document's own 256 KiB data cap.
                if (zipEntry.Length > AppDataStore.MaxDataBytes + 4096)
                {
                    continue;
                }

                AppDataDocumentDto? dto;
                try
                {
                    using var entryStream = zipEntry.Open();
                    dto = JsonSerializer.Deserialize(entryStream, AppJsonContext.Default.AppDataDocumentDto);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (dto?.Data is null)
                {
                    continue;
                }
                entries.Add(new Entry { AppId = appId, Key = key, Data = dto.Data.Value });
            }

            return new ReadResult { ProfileJson = profileJson, AppData = entries };
        }
    }
}
