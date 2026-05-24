using Nexus.Service.Models;

namespace Nexus.Service.Models.Obs;

public sealed class ObsConfigResponse : ApiResponse
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public bool HasPassword { get; set; }
}

public sealed class ObsConfigBody
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Password { get; set; }
}

public sealed class ObsScene
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
}

public sealed class ObsStatusResponse : ApiResponse
{
    public bool Connected { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public string ActiveScene { get; set; } = "";
    public List<ObsScene> Scenes { get; set; } = new();
    public bool Recording { get; set; }
    public bool Streaming { get; set; }
    public long RecordingDurationMs { get; set; }
    public long StreamingDurationMs { get; set; }
}

public sealed class ObsSetSceneBody
{
    public string SceneName { get; set; } = "";
}
