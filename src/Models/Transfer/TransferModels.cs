namespace Nexus.Service.Models.Transfer;

public sealed class TransferSavedItem
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

public sealed class TransferItemsResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public List<TransferSavedItem> Saved { get; set; } = new();
    /// <summary>Absolute inbox folder the items were written to, for "Open folder" actions.</summary>
    public string Inbox { get; set; } = "";
}

public sealed class TransferClipboardBody
{
    public string? Text { get; set; }
}

/// <summary>
/// Push payload for the "transfer" topic. Unlike the refetch-style frames it
/// carries the event itself - there is no canonical resource to refetch.
/// </summary>
public sealed class TransferReceivedFrame
{
    public long Revision { get; set; }
    /// <summary>"file" or "clipboard".</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Sender's paired-session display name; empty for desktop-token senders.</summary>
    public string From { get; set; } = "";
    public string Inbox { get; set; } = "";
}
