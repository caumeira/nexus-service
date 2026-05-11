using Qos.Service.Media;
using Qos.Service.Models.Media;

namespace Qos.Service.Tests;

public class MediaLibrarySecurityTests
{
    [Theory]
    [InlineData("18f2-Keyboard_Glow")]
    [InlineData("abc_DEF-123")]
    public void IsValidId_AllowsImporterGeneratedIds(string id)
    {
        Assert.True(MediaLibrary.IsValidId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../settings")]
    [InlineData("nested/path")]
    [InlineData("nested\\path")]
    [InlineData("item.json")]
    [InlineData("bad%2fpath")]
    public void IsValidId_BlocksPathTraversalIds(string id)
    {
        Assert.False(MediaLibrary.IsValidId(id));
    }

    [Fact]
    public void ListItems_OmitsEntriesMissingFramesOrThumbnail()
    {
        var tempDir = CreateTempDir();
        try
        {
            var library = new MediaLibrary(tempDir);
            var item = NewItem("missing-assets");
            library.SaveMeta(item);

            Assert.Empty(library.ListItems());

            File.WriteAllBytes(library.GetFramesBinPath(item.Id), new byte[] { 1 });
            Assert.Empty(library.ListItems());

            File.WriteAllBytes(library.GetThumbPath(item.Id), new byte[] { 1 });
            var listed = Assert.Single(library.ListItems());
            Assert.Equal(item.Id, listed.Id);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void DeleteItem_ReturnsTrueForMissingDirectory()
    {
        var tempDir = CreateTempDir();
        try
        {
            var library = new MediaLibrary(tempDir);

            Assert.True(library.DeleteItem("already-gone"));
            Assert.Empty(library.ListItems());
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void DeleteItem_RemovesBrokenEntryWhenAssetsAreMissing()
    {
        var tempDir = CreateTempDir();
        try
        {
            var library = new MediaLibrary(tempDir);
            var item = NewItem("broken-entry");
            library.SaveMeta(item);
            File.WriteAllBytes(library.GetThumbPath(item.Id), new byte[] { 1 });

            Assert.True(library.DeleteItem(item.Id));
            Assert.False(File.Exists(Path.Combine(library.GetItemDir(item.Id), "meta.json")));
            Assert.Empty(library.ListItems());
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static MediaItem NewItem(string id) => new()
    {
        Id = id,
        Name = id + ".gif",
        Type = "animated",
        Frames = 1,
        Fps = 1,
        Width = 160,
        Height = 90,
        ImportedAtUnixMs = 1,
    };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qos-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
