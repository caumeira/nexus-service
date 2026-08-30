using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Per-model control channel for a JPEG panel that needs more than a fixed init report:
/// a request/response handshake, or a keepalive the panel expects while streaming.
/// A model without one streams as soon as its handle opens.
///
/// Implementations are called under the hub's lock and own the device only for the duration
/// of the call. They are cheap by design: <see cref="BeforeFrame"/> runs on every frame.
/// </summary>
public interface IJpegPanelHandshake
{
    /// <summary>
    /// Runs once when a handle opens. Returning false makes the hub drop the handle rather
    /// than report a panel that will silently ignore frames.
    /// </summary>
    bool OnAttach(IHidDevice device, int reportLength);

    /// <summary>Best effort: the device is often already gone by the time this runs.</summary>
    void OnDetach(IHidDevice device, int reportLength);

    /// <summary>
    /// Runs before each frame. Returning false skips the frame, which is how a panel that
    /// has not finished booting stays unwritten without tearing the session down.
    /// </summary>
    bool BeforeFrame(IHidDevice device, int reportLength, long nowMs);
}
