using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Widgets;

/// <summary>
/// Resolved view of one installed widget. The <see cref="RootPath"/> is the
/// directory under which <c>manifest.json</c> and <c>index.html</c> live.
/// </summary>
public sealed class WidgetEntry
{
    public required string Id { get; init; }
    public required string RootPath { get; init; }
    public required WidgetManifest Manifest { get; init; }
    public required WidgetInstallPaths.Source Source { get; init; }
}
