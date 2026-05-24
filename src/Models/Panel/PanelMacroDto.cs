namespace Nexus.Service.Models.Panel;

public sealed class OpenUrlRequest
{
    public string Url { get; set; } = "";
}

public sealed class ShortcutRequest
{
    public string Keys { get; set; } = "";
}
