using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Minimum-surface RGB device controller. Talks to an OpenRGB SDK server (or
/// any compatible implementation) over TCP. AOT-safe and reflection-free.
///
/// Lifecycle is decoupled from individual operations: <see cref="TryConnectAsync"/>
/// is the only place that opens a socket. After that, all calls reuse the same
/// connection. <see cref="DeviceListChanged"/> fires whenever a fresh DEVICE_LIST_UPDATED
/// notification arrives from the server.
/// </summary>
public interface IRgbController : IAsyncDisposable
{
    /// <summary>True iff a TCP session is currently open and the protocol handshake completed.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when the SDK server sends a DEVICE_LIST_UPDATED notification or after a successful (re)connect.</summary>
    event Action? DeviceListChanged;

    /// <summary>
    /// Try to establish a TCP session with the SDK server. Returns true on success, false
    /// on connection refused / handshake failure / timeout. Safe to call repeatedly.
    /// </summary>
    Task<bool> TryConnectAsync(CancellationToken ct = default);

    /// <summary>Close the session. Idempotent.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Fetch the current device list. Returns an empty list when not connected.
    /// </summary>
    Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Switch <paramref name="device"/> into direct-control mode by sending
    /// UPDATE_MODE (1101) with the device's "Direct" (or Custom/Static) per-LED
    /// mode descriptor. This causes the OpenRGB server to call the controller's
    /// <c>DeviceUpdateMode()</c>, which for ENE-style DRAM controllers actually
    /// flips the hardware mode register over SMBus. The lighter SET_CUSTOM_MODE
    /// packet (1100) only updates the server's in-memory active_mode and never
    /// reaches the hardware, which is why per-LED writes silently fail on RGB
    /// RAM until UPDATE_MODE is used. Required before any UPDATE_LEDS push will
    /// be honored on devices that have a non-direct mode active.
    /// </summary>
    Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default);

    /// <summary>
    /// Push a frame of colors to the device. The colors array length must equal
    /// the device's <see cref="RgbDevice.LedCount"/>.
    /// </summary>
    Task PushFrameAsync(int deviceIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default);

    /// <summary>Convenience: push an all-black frame.</summary>
    Task SetOffAsync(int deviceIndex, int ledCount, CancellationToken ct = default);

    /// <summary>
    /// Push a frame of colors to a single zone within the device (OpenRGB opcode
    /// UPDATEZONELEDS). Lets us update one motherboard ARGB header without
    /// racing against the others on the same controller.
    /// </summary>
    Task PushZoneFrameAsync(int deviceIndex, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default);

    /// <summary>
    /// Ask the OpenRGB server to reconfigure an ARGB zone's LED count (opcode
    /// RESIZEZONE). ARGB is one-way so the count must be user-configured; this
    /// lets us apply the user's choice live.
    /// </summary>
    Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newSize, CancellationToken ct = default);
}
