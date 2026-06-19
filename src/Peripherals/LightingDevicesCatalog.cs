using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals;

/// <summary>
/// The full catalog of RGB-capable devices OpenRGB can drive, extracted at build
/// time from openrgb-headless/Controllers/**/*Detect*.cpp and embedded as a JSON
/// resource.
///
/// Read once on first access and cached for the process lifetime.
/// </summary>
public static class LightingDevicesCatalog
{
    private static IReadOnlyList<SupportedDeviceDto>? _cache;
    private static readonly object _gate = new();

    public static IReadOnlyList<SupportedDeviceDto> All
    {
        get
        {
            if (_cache is not null)
            {
                return _cache;
            }
            lock (_gate)
            {
                _cache ??= Load();
                return _cache;
            }
        }
    }

    private static IReadOnlyList<SupportedDeviceDto> Load()
    {
        var asm = typeof(LightingDevicesCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("openrgb-supported-devices.json");
        if (stream is null)
        {
            return new List<SupportedDeviceDto>();
        }

        OpenRgbSupportedDevicesFile? file;
        try
        {
            file = JsonSerializer.Deserialize(stream, AppJsonContext.Default.OpenRgbSupportedDevicesFile);
        }
        catch
        {
            return new List<SupportedDeviceDto>();
        }

        if (file?.Devices is null)
        {
            return new List<SupportedDeviceDto>();
        }

        var result = new List<SupportedDeviceDto>(file.Devices.Count);
        foreach (var d in file.Devices)
        {
            var (vendor, model) = SplitVendorModel(d.Name);
            result.Add(new SupportedDeviceDto
            {
                Vendor = vendor,
                Model = model,
                Category = CategoryFromController(d.Controller, d.Kind),
                VendorId = d.Vid ?? "-",
                ProductId = d.Pid ?? "-",
                Capabilities = new List<string> { d.Kind ?? "generic" },
            });
        }
        return result;
    }

    private static (string vendor, string model) SplitVendorModel(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ("", "");
        }
        var sp = name.IndexOf(' ');
        if (sp <= 0)
        {
            return ("", name);
        }
        return (name.Substring(0, sp), name.Substring(sp + 1));
    }

    private static string CategoryFromController(string? controller, string? kind)
    {
        var c = controller ?? "";
        if (c.Contains("Keyboard"))
            return "keyboard";
        if (c.Contains("Mouse") && !c.Contains("Mousemat"))
            return "mouse";
        if (c.Contains("Mousemat"))
            return "mousemat";
        if (c.Contains("Headset") || c.Contains("Microphone"))
            return "headset";
        if (c.Contains("GPU"))
            return "gpu";
        if (c.Contains("DRAM") || c.Contains("Memory") || c.Contains("Vengeance") || c.Contains("Fury"))
            return "memory";
        if (c.Contains("Hydro") || c.Contains("Kraken") || c.Contains("AIO"))
            return "aio";
        if (c.Contains("Fan") || c.Contains("Riing") || c.Contains("Hue"))
            return "fan";
        if (c.Contains("Monitor") || c.Contains("Optix"))
            return "monitor";
        if (c.Contains("Motherboard") || c.Contains("Aura") || c.Contains("MysticLight") || c.Contains("Fusion") || c.Contains("Polychrome"))
            return "motherboard";
        if (c.Contains("Case") || c.Contains("Nexus"))
            return "case";
        if (c.Contains("Gamepad"))
            return "gamepad";
        if (c.Contains("LightStrip") || c.Contains("LEDStrip") || c.Contains("LightBar") || c.Contains("Wiz") || c.Contains("Hue") || c.Contains("Yeelight") || c.Contains("Nanoleaf") || c.Contains("LIFX") || c.Contains("Govee"))
            return "light";
        return kind switch
        {
            "i2c" => "motherboard",
            _ => "controller",
        };
    }
}

public sealed class OpenRgbSupportedDevicesFile
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("devices")] public List<OpenRgbSupportedDeviceEntry> Devices { get; set; } = new();
}

public sealed class OpenRgbSupportedDeviceEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vid")] public string? Vid { get; set; }
    [JsonPropertyName("pid")] public string? Pid { get; set; }
    [JsonPropertyName("controller")] public string? Controller { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}
