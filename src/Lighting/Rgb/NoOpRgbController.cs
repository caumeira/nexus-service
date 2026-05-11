using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Qos.Service.Lighting.Rgb;

/// <summary>
/// Cross-platform fallback used when no OpenRGB binary is bundled (macOS) or
/// when device support is otherwise unavailable. Always reports zero devices
/// and silently swallows all push operations.
/// </summary>
public sealed class NoOpRgbController : IRgbController
{
    public bool IsConnected => false;
    public event Action? DeviceListChanged { add { } remove { } }

    public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

    public Task SetDirectModeAsync(int deviceIndex, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushFrameAsync(int deviceIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOffAsync(int deviceIndex, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushZoneFrameAsync(int deviceIndex, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeZoneAsync(int deviceIndex, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => default;
}
