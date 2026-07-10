namespace Nexus.Service.Panel.Streams;

/// <summary>One decoded ingest frame: flags plus the raw payload bytes.</summary>
public sealed class StreamFrame
{
    public required byte Flags { get; init; }
    public required byte[] Payload { get; init; }

    public bool IsIdr => (Flags & StreamFraming.FlagIdr) != 0;
    public bool IsControl => (Flags & StreamFraming.FlagControl) != 0;
}
