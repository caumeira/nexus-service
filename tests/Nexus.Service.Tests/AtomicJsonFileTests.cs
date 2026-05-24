using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class AtomicJsonFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicJsonFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-atomic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_string_creates_file_with_contents()
    {
        var path = Path.Combine(_tempDir, "a.json");
        AtomicJsonFile.Write(path, "{\"hello\":1}");
        Assert.True(File.Exists(path));
        Assert.Equal("{\"hello\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_string_replaces_existing_file()
    {
        var path = Path.Combine(_tempDir, "b.json");
        File.WriteAllText(path, "old");
        AtomicJsonFile.Write(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Write_bytes_replaces_existing_file()
    {
        var path = Path.Combine(_tempDir, "c.bin");
        File.WriteAllText(path, "old");
        AtomicJsonFile.Write(path, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_does_not_leave_tmp_artifact_on_success()
    {
        var path = Path.Combine(_tempDir, "d.json");
        AtomicJsonFile.Write(path, "{}");
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Concurrent_writes_to_distinct_files_dont_collide()
    {
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var path = Path.Combine(_tempDir, $"concurrent-{i}.json");
            AtomicJsonFile.Write(path, $"value-{i}");
            Assert.Equal($"value-{i}", File.ReadAllText(path));
        })).ToArray();
        await Task.WhenAll(tasks);
    }
}
