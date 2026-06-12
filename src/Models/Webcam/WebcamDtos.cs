namespace Nexus.Service.Models.Webcam;

public sealed class WebcamStartRequest
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>"h264" or "mjpeg".</summary>
    public string Codec { get; set; } = "";
}

public sealed class WebcamStatusResponse
{
    /// <summary>A start call armed the session and the virtual camera is up.</summary>
    public bool Active { get; set; }
    /// <summary>A phone stream socket is currently connected.</summary>
    public bool Streaming { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Codec { get; set; } = "";
    public long FramesReceived { get; set; }
    public long LastFrameUnixMs { get; set; }
    public string CameraName { get; set; } = "";
}
