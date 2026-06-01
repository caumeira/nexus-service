using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// The durability guarantees <see cref="AtomicJsonFile"/> exists for: an
/// overwrite is all-or-nothing and never leaks a <c>.tmp</c>, the target is
/// created when absent, and a stale orphan <c>.tmp</c> left by a prior crash is
/// consumed rather than read or leaked.
/// </summary>
public sealed class AtomicJsonFileDurabilityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AtomicJsonFileDurabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-atomic-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "data.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Overwrite_replaces_content_and_leaves_no_tmp()
    {
        AtomicJsonFile.Write(_path, "v1");
        AtomicJsonFile.Write(_path, "v2");

        Assert.Equal("v2", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"), "tmp must not leak after a successful write");
    }

    [Fact]
    public void Creates_target_when_absent()
    {
        AtomicJsonFile.Write(_path, "hello");

        Assert.Equal("hello", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Recovers_from_an_orphan_tmp_left_by_a_crash()
    {
        File.WriteAllText(_path, "old-good");
        File.WriteAllText(_path + ".tmp", "garbage-partial"); // simulate crash mid-write

        AtomicJsonFile.Write(_path, "new-good");

        Assert.Equal("new-good", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"), "the orphan tmp must be consumed");
    }
}
