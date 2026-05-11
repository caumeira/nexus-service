using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Qos.Service.Models.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Qos.Service.Sensors;

/// <summary>
/// Windows sensor provider backed by LibreHardwareMonitorLib. Reads CPU, GPU,
/// Memory, Storage, Motherboard sensors including temperatures, fan speeds,
/// voltages, clock speeds, and load — full parity with what the original
/// control service provided.
///
/// Uses the shared LhmComputer singleton for hardware access so the Computer
/// instance is shared with WindowsFanControlProvider. Updates are cheap
/// (~1-5ms per cycle) because LHM caches hardware handles.
///
/// On non-Windows platforms this class should never be instantiated — the
/// factory in Program.cs gates on RuntimeInformation.IsOSPlatform.
/// </summary>
public sealed class LibreHardwareSensorProvider : ISensorProvider
{
    private readonly LhmComputer _lhm;
    private string? _ramBrandModel;
    private string? _storageBrandModel;

    public LibreHardwareSensorProvider(LhmComputer lhm)
    {
        _lhm = lhm;
    }

    public string GetCpuModel()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var cpu = FindHardware(HardwareType.Cpu).FirstOrDefault();
        return cpu?.Name ?? "";
    }

    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        return FindHardware(HardwareType.Cpu)
            .SelectMany(hw => MapSensors(hw))
            .ToList();
    }

    public (bool Healthy, float DistanceToTJMax) GetCpuHealth()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var cpu = FindHardware(HardwareType.Cpu).FirstOrDefault();
        if (cpu is null) return (true, 0f);

        var tjMax = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault();

        var maxTemp = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault();

        if (tjMax.HasValue && maxTemp.HasValue)
        {
            var distance = tjMax.Value - maxTemp.Value;
            return (distance > 10, distance);
        }

        return (true, 0f);
    }

    public IReadOnlyList<string> GetGpuModels()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        return FindHardware(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel)
            .Select(hw => hw.Name)
            .ToList();
    }

    public IReadOnlyList<HardwareSensor> GetGpuSensors()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        return FindHardware(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel)
            .SelectMany(hw => MapSensors(hw))
            .ToList();
    }

    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        return FindHardware(HardwareType.Memory)
            .SelectMany(hw => MapSensors(hw))
            .ToList();
    }

    public string GetMemoryTotalFormatted()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        // LHM reports both physical RAM and virtual memory (pagefile) as HardwareType.Memory.
        // Physical RAM has identifier "/ram", virtual has "/vram". Pick physical.
        var mem = FindHardware(HardwareType.Memory)
            .FirstOrDefault(h => h.Identifier.ToString() == "/ram")
            ?? FindHardware(HardwareType.Memory).FirstOrDefault();
        if (mem is null) return "";

        var used = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase));
        var avail = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase));

        if (used?.Value != null && avail?.Value != null)
            return $"{used.Value.Value + avail.Value.Value:F1} GB";

        return "";
    }

    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents()
    {
        var result = new Dictionary<string, StorageComponent>();
        foreach (var di in System.IO.DriveInfo.GetDrives())
        {
            if (!di.IsReady || di.DriveType != System.IO.DriveType.Fixed) continue;
            if (di.TotalSize < 1L * 1024 * 1024 * 1024) continue;
            var label = di.Name.TrimEnd('\\');
            var totalGb = di.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var freeGb = di.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var usedGb = totalGb - freeGb;
            var usePct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;
            result[label] = new StorageComponent
            {
                Id = label,
                Name = di.VolumeLabel.Length > 0 ? $"{di.VolumeLabel} ({label})" : label,
                Capacity = FormatGb(totalGb),
                FreeSpace = FormatGb(freeGb),
                UsedSpace = FormatGb(usedGb),
                UsedPercentage = $"{usePct:F0}%",
                Format = di.DriveFormat,
                Sensors = new List<HardwareSensor>
                {
                    MakeSensor($"storage/{label}/used", "Used", "Data", (float)usedGb, "GB", label),
                    MakeSensor($"storage/{label}/free", "Free", "Data", (float)freeGb, "GB", label),
                    MakeSensor($"storage/{label}/usage", "Usage", "Level", (float)usePct, "%", label),
                },
            };
        }
        return result;
    }

    public IReadOnlyList<string> GetStoragePartitions()
    {
        return System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed && d.TotalSize >= 1L * 1024 * 1024 * 1024)
            .Select(d => d.Name).ToList();
    }

    public IReadOnlyList<StorageDriveInfo> GetStorageInfo()
    {
        return System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed && d.TotalSize >= 1L * 1024 * 1024 * 1024)
            .Select(d => new StorageDriveInfo
            {
                Name = d.VolumeLabel.Length > 0 ? $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})" : d.Name.TrimEnd('\\'),
                Partition = d.Name,
                Capacity = FormatGb(d.TotalSize / (1024.0 * 1024.0 * 1024.0)),
            }).ToList();
    }

    public IReadOnlyList<HardwareSensor> GetMotherboardSensors()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        var mobo = FindHardware(HardwareType.Motherboard).FirstOrDefault();
        if (mobo is null) return Array.Empty<HardwareSensor>();

        // Motherboard has sub-hardware (IO chips) with the real sensors
        var sensors = new List<HardwareSensor>();
        sensors.AddRange(MapSensors(mobo));
        foreach (var sub in mobo.SubHardware)
        {
            sub.Update();
            sensors.AddRange(MapSensors(sub));
        }
        return sensors;
    }

    public string GetMotherboardModel()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));
        return FindHardware(HardwareType.Motherboard).FirstOrDefault()?.Name ?? "";
    }

    public string GetRamBrandModel()
    {
        if (_ramBrandModel is not null) return _ramBrandModel;
        // PowerShell CIM is faster + AOT-safer than System.Management WMI. Pull
        // Manufacturer + PartNumber for the first physical DIMM. Multi-DIMM rigs
        // almost always mix kits from the same SKU so a single module is enough
        // for catalog matching; we can expand to a list if that turns out wrong.
        var csv = ShellOut("powershell.exe", 5000,
            "-NoProfile", "-Command",
            "Get-CimInstance -ClassName Win32_PhysicalMemory | Select-Object -First 1 Manufacturer,PartNumber | ConvertTo-Csv -NoTypeInformation");
        _ramBrandModel = ParseCsvBrandModel(csv);
        return _ramBrandModel;
    }

    public string GetStorageBrandModel()
    {
        if (_storageBrandModel is not null) return _storageBrandModel;
        // Get-PhysicalDisk surfaces the actual brand + model for both NVMe and
        // SATA drives ("Samsung SSD 980 PRO 1TB"). First non-removable disk is
        // the system drive in the overwhelming majority of desktop configs.
        var csv = ShellOut("powershell.exe", 5000,
            "-NoProfile", "-Command",
            "Get-PhysicalDisk | Where-Object { $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' } | Select-Object -First 1 Manufacturer,Model | ConvertTo-Csv -NoTypeInformation");
        _storageBrandModel = ParseCsvBrandModel(csv);
        if (string.IsNullOrEmpty(_storageBrandModel))
        {
            // Fallback for older PowerShell variants: Win32_DiskDrive.
            var fallback = ShellOut("powershell.exe", 5000,
                "-NoProfile", "-Command",
                "Get-CimInstance -ClassName Win32_DiskDrive | Where-Object { $_.MediaType -eq 'Fixed hard disk media' } | Select-Object -First 1 Manufacturer,Model | ConvertTo-Csv -NoTypeInformation");
            _storageBrandModel = ParseCsvBrandModel(fallback);
        }
        return _storageBrandModel;
    }

    /// <summary>
    /// ConvertTo-Csv output on a 2-column (Manufacturer, Model) select gives us
    /// two quoted-or-bare lines: header row, then the values row. Strip quotes,
    /// drop sentinel values like "Standard disk drives" / "Not Specified", join
    /// manufacturer + model into a single matcher string. Returns "" if neither
    /// column yields useful content.
    /// </summary>
    private static string ParseCsvBrandModel(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return "";
        var rows = csv.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (rows.Count < 2) return "";
        var vals = SplitCsvRow(rows[1]);
        if (vals.Count < 2) return "";
        var mfg = CleanField(vals[0]);
        var model = CleanField(vals[1]);
        if (string.IsNullOrEmpty(mfg) && string.IsNullOrEmpty(model)) return "";
        if (string.IsNullOrEmpty(mfg)) return model;
        if (string.IsNullOrEmpty(model)) return mfg;
        // Avoid "Samsung Samsung 980 PRO" when the model already starts with
        // the manufacturer, which PowerShell's Model column often does.
        if (model.StartsWith(mfg, StringComparison.OrdinalIgnoreCase)) return model;
        return $"{mfg} {model}";
    }

    private static List<string> SplitCsvRow(string row)
    {
        var cells = new List<string>();
        var i = 0;
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;
        while (i < row.Length)
        {
            var c = row[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"') { cur.Append('"'); i += 2; continue; }
                if (c == '"') { inQuotes = false; i++; continue; }
                cur.Append(c); i++;
            }
            else
            {
                if (c == '"') { inQuotes = true; i++; continue; }
                if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); i++; continue; }
                cur.Append(c); i++;
            }
        }
        cells.Add(cur.ToString());
        return cells;
    }

    private static string CleanField(string v)
    {
        var s = v.Trim();
        // Common sentinel placeholders PowerShell surfaces when the BIOS / SPD
        // chip didn't populate the field. Treat as unknown.
        if (s.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("Standard disk drives", StringComparison.OrdinalIgnoreCase)) return "";
        if (s.Equals("(Standard disk drives)", StringComparison.OrdinalIgnoreCase)) return "";
        return s;
    }

    private static string ShellOut(string fileName, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            return stdout;
        }
        catch { return ""; }
    }

    public IReadOnlyList<HardwareSensor> GetFpsSensors() => Array.Empty<HardwareSensor>();

    public SensorExtras GetSensorExtras()
    {
        _lhm.Update(TimeSpan.FromMilliseconds(100));

        var extras = new SensorExtras();

        foreach (var hw in _lhm.Instance.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Battery:
                    extras.Batteries.Add(BuildComponent(hw));
                    break;
                case HardwareType.Network:
                    extras.Nics.Add(BuildComponent(hw));
                    break;
                case HardwareType.Cooler:
                    extras.Coolers.Add(BuildComponent(hw));
                    break;
                case HardwareType.Psu:
                    extras.Psus.Add(BuildComponent(hw));
                    break;
                case HardwareType.Storage:
                    extras.NvmeStorage.Add(BuildComponent(hw));
                    break;
                case HardwareType.EmbeddedController:
                    extras.EmbeddedControllers.Add(BuildComponent(hw));
                    break;
            }
        }

        return extras;
    }

    private static HardwareComponent BuildComponent(IHardware hw)
    {
        // Sub-hardware (e.g. SuperIO chips on motherboards) carries the actual
        // sensors for some HardwareTypes; walk one level so we surface them all.
        var sensors = MapSensors(hw);
        foreach (var sub in hw.SubHardware)
        {
            sub.Update();
            sensors.AddRange(MapSensors(sub));
        }
        return new HardwareComponent
        {
            Id = hw.Identifier.ToString(),
            Name = string.IsNullOrWhiteSpace(hw.Name) ? hw.HardwareType.ToString() : hw.Name,
            Sensors = sensors,
        };
    }

    public string GetOsVersion() => RuntimeInformation.OSDescription;

    public void SetPollingRate(int pollingRate) { }

    // ── Helpers ──

    private IEnumerable<IHardware> FindHardware(params HardwareType[] types)
    {
        return _lhm.Instance.Hardware.Where(h => types.Contains(h.HardwareType));
    }

    private static List<HardwareSensor> MapSensors(IHardware hw)
    {
        return hw.Sensors.Select(s => new HardwareSensor
        {
            Id = s.Identifier.ToString(),
            Name = s.Name,
            Type = MapSensorType(s.SensorType),
            Value = s.Value ?? 0f,
            Min = s.Min ?? 0f,
            Max = s.Max ?? 0f,
            Units = MapUnits(s.SensorType),
            Formatted = FormatValue(s.Value ?? 0f, s.SensorType),
            FormattedMax = FormatValue(s.Max ?? 0f, s.SensorType),
            FormattedMin = FormatValue(s.Min ?? 0f, s.SensorType),
            Parent = new SensorParent { Id = hw.Identifier.ToString(), Name = hw.Name },
        }).ToList();
    }

    private static string MapSensorType(SensorType type) => type switch
    {
        SensorType.Voltage => "Voltage",
        SensorType.Current => "Current",
        SensorType.Clock => "Clock",
        SensorType.Temperature => "Temperature",
        SensorType.Load => "Load",
        SensorType.Frequency => "Frequency",
        SensorType.Fan => "Fan",
        SensorType.Flow => "Flow",
        SensorType.Control => "Control",
        SensorType.Level => "Level",
        SensorType.Factor => "Factor",
        SensorType.Power => "Power",
        SensorType.Data => "Data",
        SensorType.SmallData => "SmallData",
        SensorType.Throughput => "Throughput",
        SensorType.TimeSpan => "TimeSpan",
        SensorType.Energy => "Energy",
        SensorType.Noise => "Noise",
        _ => type.ToString(),
    };

    private static string MapUnits(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Power => "W",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        _ => "",
    };

    private static string FormatValue(float value, SensorType type) => type switch
    {
        SensorType.Temperature => $"{value:F1} °C",
        SensorType.Load or SensorType.Control or SensorType.Level => $"{value:F1}%",
        SensorType.Clock => $"{value:F0} MHz",
        SensorType.Voltage => $"{value:F3} V",
        SensorType.Current => $"{value:F3} A",
        SensorType.Fan => $"{value:F0} RPM",
        SensorType.Power => $"{value:F1} W",
        SensorType.Data => $"{value:F2} GB",
        SensorType.SmallData => $"{value:F0} MB",
        SensorType.Throughput => $"{value:F0} B/s",
        _ => $"{value:F1}",
    };

    private static string FormatGb(double gb) => gb >= 1000 ? $"{gb / 1024.0:F2} TB" : $"{gb:F2} GB";

    private static HardwareSensor MakeSensor(string id, string name, string type, float value, string units, string parentName)
    {
        var formatted = type switch
        {
            "Load" or "Level" => $"{value:F1}{units}",
            "Data" => $"{value:F2} {units}",
            _ => value > 0 ? $"{value:F0}" : "",
        };
        return new HardwareSensor
        {
            Id = id, Name = name, Type = type, Value = value, Units = units,
            Formatted = formatted,
            Parent = new SensorParent { Id = id.Split('/')[0], Name = parentName },
        };
    }
}
