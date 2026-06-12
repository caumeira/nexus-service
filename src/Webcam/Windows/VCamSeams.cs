using System;

namespace Nexus.Service.Webcam.Windows;

/// <summary>
/// Seam over the NexusVCam.dll flat control exports (ControlApi.cpp) so the
/// camera lifecycle is unit-testable off Windows.
/// </summary>
internal interface IVCamControl
{
    /// <summary>Throws with an actionable message when the media source DLL is missing or its CLSID is not registered.</summary>
    void EnsureAvailable();

    /// <summary>HRESULT of camera creation; on success the handle owns the live IMFVirtualCamera.</summary>
    int Create(string friendlyName, bool allUsers, out nint handle);

    int Destroy(nint handle);
}

/// <summary>Producer view of the shared-memory frame ring plus its wake-hint event.</summary>
internal interface IVCamFrameRing : IDisposable
{
    Memory<byte> Mapping { get; }

    void SignalFrameReady();
}

internal interface IVCamFrameRingFactory
{
    IVCamFrameRing Create(int width, int height, int slotCount);
}
