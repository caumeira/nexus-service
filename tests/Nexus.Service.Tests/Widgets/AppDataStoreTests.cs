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

        Assert.Equal(3, imported.Revision);
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
}
