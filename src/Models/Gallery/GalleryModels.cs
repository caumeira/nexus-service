using System.Collections.Generic;

namespace Nexus.Service.Models.Gallery;

/// <summary>Values for <see cref="GallerySource.Kind"/>.</summary>
public static class GallerySourceKinds
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string Upload = "upload";
}

public sealed class GallerySource
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long AddedAtUnixMs { get; set; }
}

/// <summary>Persistence shape of gallery/sources.json.</summary>
public sealed class GallerySourcesFile
{
    public List<GallerySource> Sources { get; set; } = new();
}

public sealed class GallerySourcesResponse
{
    public List<GallerySource> Sources { get; set; } = new();
}

public sealed class AddGallerySourceBody
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class GallerySourceMutationResponse
{
    public GallerySource? Source { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
}

public sealed class GalleryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
}

public sealed class GalleryItemsResponse
{
    public List<GalleryItem> Items { get; set; } = new();
}

public sealed class GalleryBrowseEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class GalleryBrowseResponse
{
    public string Path { get; set; } = "";
    public string? Parent { get; set; }
    public List<GalleryBrowseEntry> Dirs { get; set; } = new();
    public List<GalleryBrowseEntry> Files { get; set; } = new();
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
}
