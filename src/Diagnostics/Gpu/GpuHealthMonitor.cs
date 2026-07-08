using System;
using System.Collections.Generic;

namespace Nexus.Service.Diagnostics.Gpu;

/// <summary>Throttle reason keys currently active, plus cumulative violation-time
/// counters (microseconds) for the policies NVML exposes. Only Power, Thermal and
/// BoardLimit have a clean 1:1 NVML violation-status source (see GpuHealthMonitor
/// remarks); HwThermalUs has none and is always null.</summary>
public sealed record GpuThrottleInfo(
    IReadOnlyList<string> Active,
    long? SwPowerCapUs,
    long? SwThermalUs,
    long? HwThermalUs,
    long? HwPowerBrakeUs);

/// <summary>Per-GPU health readout. RecentTdrCount/RecentDriverErrorCount are
/// intentionally absent - the integrator fills those from the event monitor.</summary>
public sealed record GpuInfo(
    string Name,
    string? DriverVersion,
    double? TemperatureC,
    double? PowerW,
    GpuThrottleInfo Throttle);

public sealed record GpuHealthSnapshot(bool Supported, IReadOnlyList<GpuInfo> Gpus)
{
    public static readonly GpuHealthSnapshot Unsupported = new(false, Array.Empty<GpuInfo>());
}

/// <summary>
/// NVML-backed GPU health monitor: temperature, power draw, and clocks
/// throttle/violation counters for every NVIDIA GPU. Windows-only (nvml.dll is
/// the Windows driver's management library); self-gates via
/// <see cref="OperatingSystem.IsWindows"/> so it is safe to construct and call
/// unconditionally from cross-platform callers, same as
/// <see cref="Nexus.Service.Sensors.LibreHardwareSensorProvider"/>'s factory-gated
/// pattern but folded into one class instead of a per-OS implementation.
///
/// Snapshot() is lazy and cached for 30s. NVML init is attempted at most once;
/// on failure (no driver, load error) the monitor marks itself permanently
/// unsupported and only retries once per hour, so a GPU-less box never repeatedly
/// pays the native-load cost.
/// </summary>
public sealed class GpuHealthMonitor
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitRetryInterval = TimeSpan.FromHours(1);

    // Floor below which a forceRefresh request is served from cache anyway, so
    // a stuck client retry loop cannot make this re-query NVML continuously.
    private static readonly TimeSpan ForceRefreshFloor = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private GpuHealthSnapshot _cached = GpuHealthSnapshot.Unsupported;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    private bool _initTried;
    private bool _initOk;
    private DateTime _initFailedAtUtc = DateTime.MinValue;

    public GpuHealthSnapshot Snapshot(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return GpuHealthSnapshot.Unsupported;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var honorForce = forceRefresh && now - _cachedAtUtc >= ForceRefreshFloor;
            if (!honorForce && now - _cachedAtUtc < RefreshInterval)
            {
                return _cached;
            }

            if (!EnsureInitialized(now))
            {
                _cached = GpuHealthSnapshot.Unsupported;
                _cachedAtUtc = now;
                return _cached;
            }

            _cached = ReadSnapshot();
            _cachedAtUtc = now;
            return _cached;
        }
    }

    // Caller holds _gate.
    private bool EnsureInitialized(DateTime now)
    {
        if (_initTried && !_initOk && now - _initFailedAtUtc < InitRetryInterval)
        {
            return false;
        }

        if (_initTried && _initOk)
        {
            return true;
        }

        _initTried = true;
        try
        {
            _initOk = NvmlInterop.TryLoad() && NvmlInterop.Init() == NvmlInterop.Success;
        }
        catch
        {
            _initOk = false;
        }

        if (!_initOk)
        {
            _initFailedAtUtc = now;
        }
        return _initOk;
    }

    private static GpuHealthSnapshot ReadSnapshot()
    {
        try
        {
            if (NvmlInterop.DeviceGetCount(out var count) != NvmlInterop.Success)
            {
                return GpuHealthSnapshot.Unsupported;
            }

            var driverVersion = NvmlInterop.GetDriverVersion();
            var gpus = new List<GpuInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (NvmlInterop.DeviceGetHandleByIndex(i, out var device) != NvmlInterop.Success)
                {
                    continue;
                }
                gpus.Add(ReadDevice(device, driverVersion));
            }
            return new GpuHealthSnapshot(true, gpus);
        }
        catch
        {
            // Catches managed exceptions only (e.g. a bad marshalled buffer) - a
            // native fault from a stale or null function pointer crashes the
            // process before this catch could ever run.
            return GpuHealthSnapshot.Unsupported;
        }
    }

    private static GpuInfo ReadDevice(IntPtr device, string? driverVersion)
    {
        var name = NvmlInterop.GetDeviceName(device);

        double? tempC = NvmlInterop.GetTemperature(device, out var temp) == NvmlInterop.Success ? temp : null;
        double? powerW = NvmlInterop.GetPowerUsageMilliwatts(device, out var mw) == NvmlInterop.Success ? mw / 1000.0 : null;

        string[] active = Array.Empty<string>();
        if (NvmlInterop.GetClocksReasons(device, out var reasons) == NvmlInterop.Success)
        {
            active = NvmlInterop.MapReasonsToActiveKeys(reasons);
        }

        long? swPowerCapUs = ReadViolationUs(device, NvmlInterop.PolicyPower);
        long? swThermalUs = ReadViolationUs(device, NvmlInterop.PolicyThermal);
        long? hwPowerBrakeUs = ReadViolationUs(device, NvmlInterop.PolicyBoardLimit);

        var throttle = new GpuThrottleInfo(active, swPowerCapUs, swThermalUs, null, hwPowerBrakeUs);
        return new GpuInfo(name, driverVersion, tempC, powerW, throttle);
    }

    private static long? ReadViolationUs(IntPtr device, int policyType)
    {
        var rc = NvmlInterop.GetViolationStatus(device, policyType, out var v);
        return rc == NvmlInterop.Success ? (long)v.ViolationTimeUs : null;
    }
}
