using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Widgets;

/// <summary>src/Widgets/AppDataStore.cs: CAS, size/key-count limits, concurrent-write serialisation.</summary>
public sealed class AppDataStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AppDataStore _store;

    public AppDataStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-appdata-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new AppDataStore(() => _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void Get_on_an_absent_document_returns_revision_zero_and_null_data()
    {
        var (revision, updatedAt, data) = _store.Get("com.test.app", "save");
        Assert.Equal(0, revision);
        Assert.Equal("", updatedAt);
        Assert.Null(data);
    }

    [Fact]
    public void Put_with_baseRevision_zero_creates_the_first_revision()
    {
        var result = _store.Put("com.test.app", "save", 0, Json("""{"a":1}"""));
        Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
        Assert.Equal(1, result.Revision);

        var (revision, _, data) = _store.Get("com.test.app", "save");
        Assert.Equal(1, revision);
        Assert.Equal("1", data!.Value.GetProperty("a").GetRawText());
    }

    [Fact]
    public void Put_with_a_stale_baseRevision_is_rejected_with_the_current_document()
    {
        _store.Put("com.test.app", "save", 0, Json("""{"a":1}"""));

        var stale = _store.Put("com.test.app", "save", 0, Json("""{"a":2}"""));

        Assert.Equal(AppDataStore.PutOutcome.Conflict, stale.Outcome);
        Assert.Equal(1, stale.Revision);
        Assert.Equal("1", stale.Data!.Value.GetProperty("a").GetRawText());

        // The rejected write never landed.
        var (revision, _, data) = _store.Get("com.test.app", "save");
        Assert.Equal(1, revision);
        Assert.Equal("1", data!.Value.GetProperty("a").GetRawText());
    }

    [Fact]
    public void Put_rebased_on_the_current_revision_succeeds_and_advances_it()
    {
        var first = _store.Put("com.test.app", "save", 0, Json("""{"a":1}"""));
        var second = _store.Put("com.test.app", "save", first.Revision, Json("""{"a":2}"""));

        Assert.Equal(AppDataStore.PutOutcome.Ok, second.Outcome);
        Assert.Equal(2, second.Revision);
    }

    [Fact]
    public void Put_over_the_256KiB_limit_is_rejected_as_TooLarge()
    {
        var huge = "\"" + new string('x', 300 * 1024) + "\"";
        var result = _store.Put("com.test.app", "save", 0, Json(huge));
        Assert.Equal(AppDataStore.PutOutcome.TooLarge, result.Outcome);

        var (revision, _, _) = _store.Get("com.test.app", "save");
        Assert.Equal(0, revision);
    }

    [Fact]
    public void A_17th_key_for_the_same_app_is_rejected_as_TooManyKeys()
    {
        for (var i = 0; i < AppDataStore.MaxKeysPerApp; i++)
        {
            var result = _store.Put("com.test.app", $"k{i}", 0, Json("1"));
            Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
        }

        var overCap = _store.Put("com.test.app", "one-too-many", 0, Json("1"));
        Assert.Equal(AppDataStore.PutOutcome.TooManyKeys, overCap.Outcome);
    }

    [Fact]
    public void Updating_an_existing_key_never_counts_against_the_per_app_key_cap()
    {
        for (var i = 0; i < AppDataStore.MaxKeysPerApp; i++)
        {
            _store.Put("com.test.app", $"k{i}", 0, Json("1"));
        }

        // Rewriting an existing key must not be blocked by the cap it already
        // satisfies.
        var existing = _store.Get("com.test.app", "k0");
        var result = _store.Put("com.test.app", "k0", existing.Revision, Json("2"));
        Assert.Equal(AppDataStore.PutOutcome.Ok, result.Outcome);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("a")]
    [InlineData("a.b-c_d9")]
    public void Valid_keys_are_accepted(string key) => Assert.True(AppDataKeys.IsValid(key));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Save")]
    [InlineData("-save")]
    [InlineData(".save")]
    [InlineData("save/x")]
    [InlineData("save x")]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("con.bak")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("COM9")]
    [InlineData("lpt1")]
    [InlineData("lpt9.json")]
    public void Invalid_keys_are_rejected(string? key) => Assert.False(AppDataKeys.IsValid(key));

    [Fact]
    public void Invalid_key_over_max_length_is_rejected()
    {
        Assert.False(AppDataKeys.IsValid(new string('a', AppDataKeys.MaxLength + 1)));
        Assert.True(AppDataKeys.IsValid(new string('a', AppDataKeys.MaxLength)));
    }

    [Fact]
    public async Task Concurrent_puts_against_the_same_key_serialise_without_losing_a_write()
    {
        var seed = _store.Put("com.test.app", "counter", 0, Json("0"));

        // Each task reads the current revision, then retries on conflict -
        // proving the lock prevents two writers from both succeeding against
        // the same base revision (which would silently drop one write).
        var tasks = new Task[20];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                while (true)
                {
                    var (revision, _, _) = _store.Get("com.test.app", "counter");
                    var result = _store.Put("com.test.app", "counter", revision, Json((revision + 1).ToString()));
                    if (result.Outcome == AppDataStore.PutOutcome.Ok) return;
                }
            });
        }
        await Task.WhenAll(tasks);

        var expectedFinalRevision = seed.Revision + tasks.Length;
        var (finalRevision, _, data) = _store.Get("com.test.app", "counter");
        Assert.Equal(expectedFinalRevision, finalRevision);
        Assert.Equal(expectedFinalRevision.ToString(), data!.Value.GetRawText());
    }

    [Fact]
    public void Import_writes_a_revision_strictly_above_whatever_is_currently_stored()
    {
        _store.Put("com.test.app", "save", 0, Json("1"));
        _store.Put("com.test.app", "save", 1, Json("2"));

        var imported = _store.Import("com.test.app", "save", Json("99"));

        Assert.Equal(3, imported!.Revision);
        var (revision, _, data) = _store.Get("com.test.app", "save");
        Assert.Equal(3, revision);
        Assert.Equal("99", data!.Value.GetRawText());
    }

    [Fact]
    public void EnumerateAll_returns_every_written_app_and_key()
    {
        _store.Put("com.test.app", "save", 0, Json("1"));
        _store.Put("com.test.app", "prefs", 0, Json("1"));
        _store.Put("com.other.app", "save", 0, Json("1"));

        var all = _store.EnumerateAll().ToList();

        Assert.Contains(("com.test.app", "save"), all);
        Assert.Contains(("com.test.app", "prefs"), all);
        Assert.Contains(("com.other.app", "save"), all);
        Assert.Equal(3, all.Count);
    }

    // ── rule 6: cloud sync imports are conditional on the decision's local revision ─

    [Fact]
    public void Import_with_a_matching_expectedRevision_writes_and_returns_the_new_file()
    {
        var seed = _store.Put("com.test.app", "save", 0, Json("1"));

        var imported = _store.Import("com.test.app", "save", Json("2"), expectedRevision: seed.Revision);

        Assert.NotNull(imported);
        Assert.Equal(seed.Revision + 1, imported!.Revision);
    }

    [Fact]
    public void Import_with_a_stale_expectedRevision_is_skipped_and_writes_nothing()
    {
        var seed = _store.Put("com.test.app", "save", 0, Json("1"));
        _store.Put("com.test.app", "save", seed.Revision, Json("2")); // a real write happens between the decision and the import

        var imported = _store.Import("com.test.app", "save", Json("\"cloud-value\""), expectedRevision: seed.Revision);

        Assert.Null(imported);
        var (revision, _, data) = _store.Get("com.test.app", "save");
        Assert.Equal(seed.Revision + 1, revision);
        Assert.Equal("2", data!.Value.GetRawText());
    }

    // ── rule 4: every public method validates its own appId/key ─────────

    [Theory]
    [InlineData("NotValid", "save")]
    [InlineData("com.test.app", "BadKey")]
    [InlineData("../escape", "save")]
    [InlineData("com.test.app", "../escape")]
    public void Every_public_method_rejects_an_invalid_appId_or_key(string appId, string key)
    {
        Assert.Throws<ArgumentException>(() => _store.Get(appId, key));
        Assert.Throws<ArgumentException>(() => _store.TryRead(appId, key));
        Assert.Throws<ArgumentException>(() => _store.Put(appId, key, 0, Json("1")));
        Assert.Throws<ArgumentException>(() => _store.Import(appId, key, Json("1")));
        Assert.Throws<ArgumentException>(() => _store.SetCloudState(appId, key, new Nexus.Service.Persistence.AppDataCloudState()));
        Assert.Throws<ArgumentException>(() => _store.Delete(appId, key));
        Assert.Throws<ArgumentException>(() => _store.ArchiveAndRemove(appId, key, "acct"));
    }

    // ── rule 5: DocumentChanged is the one broadcast hook every write path uses ─

    [Fact]
    public void DocumentChanged_fires_on_a_successful_put_but_not_on_a_rejected_one()
    {
        var fired = new List<(string AppId, string Key)>();
        _store.DocumentChanged += (appId, key) => fired.Add((appId, key));

        _store.Put("com.test.app", "save", 0, Json("1"));
        Assert.Equal(new[] { ("com.test.app", "save") }, fired);

        _store.Put("com.test.app", "save", 0, Json("2")); // stale base - conflict
        Assert.Single(fired);
    }

    [Fact]
    public void DocumentChanged_fires_on_import_delete_and_archive_but_not_on_SetCloudState()
    {
        _store.Put("com.test.app", "save", 0, Json("1"));
        var fired = new List<string>();
        _store.DocumentChanged += (_, key) => fired.Add(key);

        _store.Import("com.test.app", "save", Json("2"));
        Assert.Equal(new[] { "save" }, fired);

        _store.SetCloudState("com.test.app", "save", new Nexus.Service.Persistence.AppDataCloudState { AccountId = "a", Revision = 1 });
        Assert.Single(fired); // no visible document change

        _store.ArchiveAndRemove("com.test.app", "save", "local");
        Assert.Equal(new[] { "save", "save" }, fired);

        _store.Put("com.test.app", "other", 0, Json("1"));
        _store.Delete("com.test.app", "other");
        Assert.Equal(new[] { "save", "save", "other", "other" }, fired);
    }

    [Fact]
    public void DocumentChanged_does_not_fire_when_deleting_a_key_that_never_existed()
    {
        var fired = 0;
        _store.DocumentChanged += (_, _) => fired++;

        _store.Delete("com.test.app", "never-written");

        Assert.Equal(0, fired);
    }

    // ── rule 2 plumbing: archive namespace, read back ────────────────────

    [Fact]
    public void ArchiveAndRemove_moves_the_document_out_of_the_live_store()
    {
        _store.Put("com.test.app", "save", 0, Json("""{"a":1}"""));

        var archived = _store.ArchiveAndRemove("com.test.app", "save", "other-account");

        Assert.True(archived);
        Assert.Null(_store.TryRead("com.test.app", "save"));
        var archivePath = Path.Combine(_root, ".archive", "other-account", "com.test.app", "save.json");
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public void ArchiveAndRemove_on_a_document_that_does_not_exist_is_a_no_op()
    {
        Assert.False(_store.ArchiveAndRemove("com.test.app", "never-written", "local"));
    }

    // ── rule 9: the per-app key-count cap is checked under a per-app lock ─

    [Fact]
    public async Task Concurrent_puts_to_distinct_new_keys_never_exceed_the_per_app_key_cap()
    {
        var tasks = new Task<AppDataStore.PutResult>[AppDataStore.MaxKeysPerApp + 8];
        for (var i = 0; i < tasks.Length; i++)
        {
            var key = $"k{i}";
            tasks[i] = Task.Run(() => _store.Put("com.test.app", key, 0, Json("1")));
        }
        await Task.WhenAll(tasks);

        var okCount = tasks.Count(t => t.Result.Outcome == AppDataStore.PutOutcome.Ok);
        Assert.Equal(AppDataStore.MaxKeysPerApp, okCount);
        Assert.Equal(AppDataStore.MaxKeysPerApp, _store.EnumerateAll().Count(t => t.AppId == "com.test.app"));
    }
}
