using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Nexus.Service.Diagnostics.Storage;

/// <summary>
/// Parses smartmontools' smartctl JSON output (`--scan --json` and `-a -j
/// &lt;device&gt;`). Field names verified against the smartctl 7.x source
/// (smartctl.cpp js_device_info/scan_devices, ataprint.cpp, nvmeprint.cpp):
/// device.name/type/protocol, model_name, serial_number, smart_status.passed,
/// user_capacity.bytes, temperature.current, power_on_time.hours,
/// power_cycle_count, ata_smart_attributes.table[] (id/name/value/worst/
/// thresh/raw.value), nvme_smart_health_information_log (critical_warning/
/// temperature/available_spare/available_spare_threshold/percentage_used/
/// data_units_read/data_units_written/power_cycles/power_on_hours/
/// unsafe_shutdowns/media_errors/num_err_log_entries).
///
/// Uses JsonDocument (no source-gen context needed - a DOM reader isn't
/// reflection-bound) so it stays AOT-safe while tolerating smartctl's loosely
/// shaped, version-dependent output. Pure parsing, no process/OS dependency,
/// so it compiles and unit-tests on every platform; only
/// <see cref="LinuxSmartHealthMonitor"/>'s shell-out is Linux-only.
/// </summary>
public static class LinuxSmartctlParser
{
    private const ulong BytesPerDataUnit = 512_000;

    public sealed record ScanEntry(string Name, string Type);

    /// <summary>Parses the `{"devices":[{"name":...,"type":...}]}` shape from `--scan --json`.</summary>
    public static List<ScanEntry> ParseScan(string json)
    {
        var result = new List<ScanEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return result;
        }

        using (doc)
        {
            if (!TryChild(doc.RootElement, "devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var dev in devices.EnumerateArray())
            {
                var name = GetStr(dev, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                result.Add(new ScanEntry(name, GetStr(dev, "type") ?? ""));
            }
        }
        return result;
    }

    /// <summary>Parses one device's `-a -j` output into the wire DTO. Null on
    /// unparseable input; never throws.</summary>
    public static SmartDriveInfo? ParseDevice(string json, ScanEntry scanEntry)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var modelName = GetStr(root, "model_name") ?? "";
            var serial = GetStr(root, "serial_number") ?? "";

            TryChild(root, "device", out var deviceObj);
            var deviceType = GetStr(deviceObj, "type") ?? scanEntry.Type;
            var protocol = GetStr(deviceObj, "protocol");
            var bus = ClassifyBus(deviceType, protocol);
            var busKind = ToBusKind(bus);

            TryChild(root, "user_capacity", out var capacityObj);
            var sizeBytes = GetULong(capacityObj, "bytes");

            TryChild(root, "temperature", out var tempObj);
            var tempC = SanitizeTemp(GetDouble(tempObj, "current"));

            TryChild(root, "power_on_time", out var powerOnObj);
            var powerOnHours = GetULong(powerOnObj, "hours");
            var powerCycles = GetULong(root, "power_cycle_count");

            TryChild(root, "smart_status", out var smartStatusObj);
            var smartPassed = GetBool(smartStatusObj, "passed");

            // smartctl still emits valid JSON when it fails to open/read a
            // device (permission error, unsupported device) - just a
            // smartctl.messages array, no model_name or smart_status. Without
            // this guard that shape mints an empty-model, zero-data drive
            // with Status "good", rendering an unreadable drive as healthy.
            if (string.IsNullOrWhiteSpace(modelName) && smartPassed is null)
            {
                return null;
            }

            var attributes = new List<SmartAttributeSample>();
            NvmeHealthLog? nvmeLog = null;

            if (busKind == StorageBusKind.Nvme)
            {
                nvmeLog = ParseNvmeLog(root);
            }
            else if (TryChild(root, "ata_smart_attributes", out var ataAttrs)
                     && TryChild(ataAttrs, "table", out var table)
                     && table.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in table.EnumerateArray())
                {
                    var sample = ParseAttribute(row);
                    if (sample is not null)
                    {
                        attributes.Add(sample);
                    }
                }
            }

            // smartctl only reports a binary pass/fail self-assessment, not
            // DiskInfoToolkit's graded Good/Caution/Warning/Bad summary, so
            // VendorHealth stays null here - a failed assessment is carried
            // through PredictsFailure instead, same as Windows does for LHM's
            // own PredictsFailure flag.
            var predictsFailure = smartPassed.HasValue ? !smartPassed.Value : (bool?)null;
            var classification = SmartClassifier.Classify(new SmartClassifierInput(
                busKind, attributes, nvmeLog, VendorHealth: null, PredictsFailure: predictsFailure));

            var flaggedIds = classification.FlaggedAttributeIds;
            var attributesWire = attributes.Select(a => new SmartAttributeWire
            {
                Id = a.Id,
                Name = a.Name,
                Current = a.Current,
                Worst = a.Worst,
                Threshold = a.Threshold,
                Raw = a.Raw,
                Flagged = flaggedIds.Contains(a.Id),
            }).ToList();

            var nvmeWire = nvmeLog is null
                ? null
                : new SmartNvmeWire
                {
                    CriticalWarning = nvmeLog.CriticalWarning,
                    AvailableSpare = nvmeLog.AvailableSpare,
                    SpareThreshold = nvmeLog.AvailableSpareThreshold,
                    PercentageUsed = nvmeLog.PercentageUsed,
                    MediaErrors = nvmeLog.MediaErrors,
                    ErrorLogEntries = nvmeLog.ErrorInfoLogEntries,
                    UnsafeShutdowns = nvmeLog.UnsafeShutdowns,
                    DataUnitsReadBytes = nvmeLog.DataUnitsReadBytes,
                    DataUnitsWrittenBytes = nvmeLog.DataUnitsWrittenBytes,
                };

            var id = !string.IsNullOrWhiteSpace(serial) ? $"storage:{serial}" : $"storage:{scanEntry.Name}";
            var name = !string.IsNullOrWhiteSpace(modelName) ? modelName : scanEntry.Name;

            return new SmartDriveInfo
            {
                Id = id,
                Name = name,
                Serial = serial,
                Bus = bus,
                SizeBytes = sizeBytes,
                TemperatureC = tempC,
                PowerOnHours = powerOnHours,
                PowerCycles = powerCycles,
                // smartctl has no direct equivalent of DiskInfoToolkit's
                // synthesized health percentage; left unset rather than
                // deriving one only for NVMe (100 - percentage_used) and
                // leaving ATA drives inconsistently null.
                HealthPercent = null,
                Status = MapStatus(classification.Status),
                StatusReasons = classification.Reasons.Select(r => r.Code).Distinct().ToList(),
                Attributes = attributesWire,
                Nvme = nvmeWire,
                DetailedReasons = classification.Reasons,
            };
        }
    }

    private static NvmeHealthLog ParseNvmeLog(JsonElement root)
    {
        TryChild(root, "nvme_smart_health_information_log", out var log);
        var tempC = GetDouble(log, "temperature");

        return new NvmeHealthLog
        {
            CriticalWarning = GetByte(log, "critical_warning") ?? 0,
            CompositeTemperatureKelvin = ToKelvinUShort(tempC),
            AvailableSpare = GetByte(log, "available_spare") ?? 0,
            AvailableSpareThreshold = GetByte(log, "available_spare_threshold") ?? 0,
            PercentageUsed = GetByte(log, "percentage_used") ?? 0,
            DataUnitsReadBytes = UnitsToBytes(GetULong(log, "data_units_read") ?? 0),
            DataUnitsWrittenBytes = UnitsToBytes(GetULong(log, "data_units_written") ?? 0),
            PowerCycles = GetULong(log, "power_cycles") ?? 0,
            PowerOnHours = GetULong(log, "power_on_hours") ?? 0,
            UnsafeShutdowns = GetULong(log, "unsafe_shutdowns") ?? 0,
            MediaErrors = GetULong(log, "media_errors") ?? 0,
            ErrorInfoLogEntries = GetULong(log, "num_err_log_entries") ?? 0,
        };
    }

    // Clamped before the cast: an out-of-range celsius value (a malformed or
    // absurd JSON number) would otherwise wrap silently through the unchecked
    // double-to-ushort conversion instead of saturating. Internal so
    // LinuxSmartctlParserTests can exercise the clamp directly - the field it
    // feeds isn't part of the wire DTO.
    internal static ushort ToKelvinUShort(double? celsius)
    {
        if (!celsius.HasValue)
        {
            return 0;
        }
        var kelvin = Math.Clamp(Math.Round(celsius.Value) + 273, 0, ushort.MaxValue);
        return (ushort)kelvin;
    }

    private static SmartAttributeSample? ParseAttribute(JsonElement row)
    {
        var id = GetByte(row, "id");
        if (id is null)
        {
            return null;
        }
        TryChild(row, "raw", out var rawObj);
        return new SmartAttributeSample(
            id.Value,
            GetStr(row, "name") ?? "",
            GetByte(row, "value") ?? 0,
            GetByte(row, "worst") ?? 0,
            GetByte(row, "thresh") ?? 0,
            GetULong(rawObj, "value") ?? 0);
    }

    // "sat"/"usbjmicron"/etc are smartctl's own -d type names; classify by
    // prefix/protocol rather than an exhaustive list of USB bridge chipsets.
    private static string ClassifyBus(string scanType, string? protocol)
    {
        if (scanType.StartsWith("usb", StringComparison.OrdinalIgnoreCase))
        {
            return "usb";
        }
        if (scanType.Contains("nvme", StringComparison.OrdinalIgnoreCase) || protocol == "NVMe")
        {
            return "nvme";
        }
        if (protocol is "ATA" or "ATA+SCSI" || scanType is "sat" or "ata")
        {
            return "sata";
        }
        // Bare SCSI/SAS and RAID-controller passthrough (megaraid/areca -d
        // syntax) fall here - RAID passthrough needs a controller-specific -d
        // argument per card model, too custom/high-maintenance to enumerate.
        return "other";
    }

    private static StorageBusKind ToBusKind(string bus) => bus switch
    {
        "nvme" => StorageBusKind.Nvme,
        "sata" => StorageBusKind.Sata,
        "usb" => StorageBusKind.Usb,
        _ => StorageBusKind.Other,
    };

    private static string MapStatus(DriveStatus status) => status switch
    {
        DriveStatus.Good => "good",
        DriveStatus.Caution => "caution",
        DriveStatus.Warning => "warning",
        DriveStatus.Bad => "bad",
        _ => "unknown",
    };

    private static ulong UnitsToBytes(ulong units)
    {
        if (units == 0)
        {
            return 0;
        }
        return units > ulong.MaxValue / BytesPerDataUnit ? ulong.MaxValue : units * BytesPerDataUnit;
    }

    private static double? SanitizeTemp(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    private static bool TryChild(JsonElement obj, string prop, out JsonElement child)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out child))
        {
            return true;
        }
        child = default;
        return false;
    }

    private static string? GetStr(JsonElement obj, string prop) =>
        TryChild(obj, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ulong? GetULong(JsonElement obj, string prop) =>
        TryChild(obj, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var u) ? u : null;

    private static double? GetDouble(JsonElement obj, string prop) =>
        TryChild(obj, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static byte? GetByte(JsonElement obj, string prop) =>
        TryChild(obj, prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var u)
            ? (byte)Math.Min(u, byte.MaxValue) : null;

    private static bool? GetBool(JsonElement obj, string prop) =>
        TryChild(obj, prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
