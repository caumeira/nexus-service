using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Cross-platform read interface for system sensor data. Implementations are
/// expected to be cheap and synchronous - controllers call these on every
/// HTTP request. Heavy work (LibreHardwareMonitor, top, etc.) should run in a
/// background sampling loop and serve cached values from these methods.
///
/// All methods return empty collections / placeholder strings rather than null
/// or throw - the SPA should always get well-formed JSON.
/// </summary>
public interface ISensorProvider
{
    string GetCpuModel();
    IReadOnlyList<HardwareSensor> GetCpuSensors();
    (bool Healthy, float DistanceToTJMax) GetCpuHealth();

    IReadOnlyList<string> GetGpuModels();
    IReadOnlyList<HardwareSensor> GetGpuSensors();

    /// <summary>
    /// One entry per physical GPU, each carrying only its own sensors plus a
    /// vendor + integrated/discrete classification. <see cref="GetGpuModels"/>
    /// and <see cref="GetGpuSensors"/> are flat views over this. The monitoring
    /// stream emits one component per entry so the client can target a specific
    /// GPU instead of whichever the platform enumerates first.
    /// </summary>
    IReadOnlyList<GpuReadout> GetGpus();

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

    /// <summary>
    /// Completes when the underlying hardware enumeration is finished and
    /// follow-up reads (CPU / motherboard / GPU model names, sensor lists) are
    /// expected to return populated data. Implementations whose hardware
    /// inspection is synchronous (shell-based Mac/Linux providers) return a
    /// completed task; the Windows LHM provider returns its background-open
    /// task. Callers that need a guaranteed-populated snapshot should await
    /// this before invoking the Get* methods.
    /// </summary>
    Task ReadyAsync(CancellationToken ct = default);
}
