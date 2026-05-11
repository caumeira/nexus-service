namespace Qos.Service.Activity;

internal static class MediaArtworkCacheKey
{
    private const string Separator = "\u001f";

    public static string Build(string source, string sessionId, string? title, string? artist, string? album)
        => string.Join(Separator, source, sessionId, title ?? "", artist ?? "", album ?? "");
}
