namespace Nexus.Service.Models.Lifecycle;

public class SetWillStartParams
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public class WillStartResponse : ApiResponse
{
    public bool Enabled { get; set; }
}

public class PawnIoStatus
{
    public bool Installed { get; set; }
    public bool Open { get; set; }
}
