using System.Collections.Concurrent;

namespace Nexus.Service.Common.ExternalTools;

public interface IAdbDeviceRegistry
{
    void Register(IAdbDeviceTarget device);
    void Unregister(string package);
    IAdbDeviceTarget? TryGet(string package);
}

/// <summary>Thread-safe map of package name to the currently connected device target.</summary>
public sealed class AdbDeviceRegistry : IAdbDeviceRegistry
{
    private readonly ConcurrentDictionary<string, IAdbDeviceTarget> _devices
        = new(System.StringComparer.Ordinal);

    public void Register(IAdbDeviceTarget device) => _devices[device.Package] = device;

    public void Unregister(string package) => _devices.TryRemove(package, out _);

    public IAdbDeviceTarget? TryGet(string package)
        => _devices.TryGetValue(package, out var target) ? target : null;
}
