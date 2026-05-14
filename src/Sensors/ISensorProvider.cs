using System.Collections.Generic;
using Qos.Service.Models.Sensors;

namespace Qos.Service.Sensors;

/// <summary>
/// Cross-platform read interface for system sensor data. Implementations are
/// expected to be cheap and synchronous — controllers call these on every
/// HTTP request. Heavy work (LibreHardwareMonitor, top, etc.) should run in a
/// background sampling loop and serve cached values from these methods.
///
/// All methods return empty collections / placeholder strings rather than null
/// or throw — the SPA should always get well-formed JSON.
/// </summary>
public interface ISensorProvider
{
    string GetCpuModel();
    IReadOnlyList<HardwareSensor> GetCpuSensors();
    (bool Healthy, float DistanceToTJMax) GetCpuHealth();

    IReadOnlyList<string> GetGpuModels();
    IReadOnlyList<HardwareSensor> GetGpuSensors();

    IReadOnlyList<HardwareSensor> GetMemorySensors();
    string GetMemoryTotalFormatted();
    /// <summary>
    /// Brand + part number of the installed RAM (e.g., "Corsair CMK16GX4M2B3000C15").
    /// Used by the benchmark / system-builder fuzzy matcher to locate the exact DIMM
    /// in the parts catalog. Returns "" when the platform can't resolve it.
    /// </summary>
    string GetRamBrandModel();

    IReadOnlyDictionary<string, StorageComponent> GetStorageComponents();
    IReadOnlyList<string> GetStoragePartitions();
    IReadOnlyList<StorageDriveInfo> GetStorageInfo();
    /// <summary>
    /// Brand + model of the primary storage drive (e.g., "Samsung SSD 970 EVO 1TB").
    /// Same matcher-oriented use as GetRamBrandModel. Returns "" when no drive is
    /// identifiable (unusual - only Linux without lshw / nvme tools).
    /// </summary>
    string GetStorageBrandModel();

    IReadOnlyList<HardwareSensor> GetMotherboardSensors();
    string GetMotherboardModel();

    /// <summary>
    /// Hardware families the Monitoring Detailed tab surfaces but no other
    /// page consumes. Implementations should walk all hardware sources they
    /// have access to (LHM exposes Battery / Network / Cooler / Psu / Storage
    /// / EmbeddedController types beyond CPU/GPU/Memory/Motherboard) and
    /// return populated lists. Platforms that can't read a given family
    /// return an empty list for it.
    /// </summary>
    SensorExtras GetSensorExtras();

    string GetOsVersion();
    void SetPollingRate(int pollingRate);
}
