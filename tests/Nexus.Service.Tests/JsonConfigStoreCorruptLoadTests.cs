using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Exercises the REAL <see cref="JsonConfigStore"/> (not a hand-written fake):
/// a corrupt settings.json must load defaults without throwing AND preserve the
/// unreadable bytes in a <c>.corrupt</c> backup so user config isn't silently
/// destroyed; and a written setting must survive a reopen.
/// </summary>
public sealed class JsonConfigStoreCorruptLoadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonConfigStoreCorruptLoadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Corrupt_file_loads_defaults_and_is_backed_up()
    {
        File.WriteAllText(_path, "{ this is not valid json ");

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        Assert.NotNull(settings); // defaults, no throw
        var backup = _path + ".corrupt";
        Assert.True(File.Exists(backup), "the corrupt file must be preserved");
        Assert.Contains("not valid json", File.ReadAllText(backup));
    }

    [Fact]
    public void Setting_round_trips_through_a_reopen()
    {
        using (var store = new JsonConfigStore(_path))
        {
            store.Update(s => s.Lighting.GlobalBrightness = 0.5f);
            store.FlushNow();
        }

        using var reopened = new JsonConfigStore(_path);
        Assert.Equal(0.5f, reopened.Load().Lighting.GlobalBrightness);
    }
}
