using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Webcam.Mac;

/// <summary>Extension states reported by the activator dylib.</summary>
internal enum CameraExtensionState
{
    NotInstalled,
    /// <summary>Activation submitted; the user must approve it in System Settings.</summary>
    PendingApproval,
    /// <summary>Installed but switched off by the user in System Settings.</summary>
    Disabled,
    /// <summary>Activation accepted; takes effect after the Mac restarts.</summary>
    RebootRequired,
    Installed,
    Failed,
}

/// <summary>
/// Seam over the activator dylib exports so extension activation is
/// unit-testable off macOS. Implementations throw with an actionable message
/// when the dylib is missing from the app bundle.
/// </summary>
internal interface ICameraExtensionActivator
{
    CameraExtensionState GetStatus();

    /// <summary>Submits the system-extension activation request and reports the resulting state.</summary>
    CameraExtensionState Activate();
}

/// <summary>
/// Spawned helper process surface: framed video frames on stdin, the ready
/// line on stdout, diagnostics on stderr.
/// </summary>
internal interface ICameraHelperProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Valid only after exit.</summary>
    int ExitCode { get; }

    Stream StandardInput { get; }

    /// <summary>Recent stderr output, captured for error surfacing.</summary>
    string StandardErrorSnapshot { get; }

    ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken);

    /// <summary>Requests clean shutdown: the helper exits on stdin EOF.</summary>
    void CloseStandardInput();

    bool WaitForExit(TimeSpan timeout);

    void Kill();
}

internal interface ICameraHelperLauncher
{
    /// <summary>Throws with an actionable message when the helper binary is missing.</summary>
    ICameraHelperProcess Launch();
}

/// <summary>
/// Helper process exit codes. Shared contract with the Swift helper's
/// ExitCode enum (Bundled/macos/camera-helper/main.swift, spec in
/// NOTES-HELPER.md); both sides must change together.
/// </summary>
internal static class CameraHelperExit
{
    public const int Clean = 0;
    public const int DeviceNotFound = 2;
    public const int SinkNotFound = 3;
    public const int SinkOpenFailed = 4;
    public const int ProtocolError = 5;
    public const int DecoderError = 6;

    public static string Describe(int code) => code switch
    {
        Clean => "camera helper exited before signaling ready",
        DeviceNotFound => "Nexus Camera device not found - the camera extension is not running; approve it in System Settings > General > Login Items & Extensions or reinstall Nexus",
        SinkNotFound => "Nexus Camera sink stream not found - the installed camera extension does not match this Nexus version; reinstall Nexus",
        SinkOpenFailed => "could not open the Nexus Camera sink stream - another feeder may hold it or the camera extension needs a restart",
        ProtocolError => "camera helper rejected the frame stream - service and helper disagree on the frame protocol",
        DecoderError => "camera helper could not decode the video stream",
        _ => $"camera helper exited with code {code}",
    };
}
