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
            return new List<SupportedDeviceDto>(FirstPartyDevices);
        }

        // First-party HYTE devices lead the list with correct model/category and a
        // "nexus" source. The bundled OpenRGB fork registers the same hardware under
        // its own HYTE* controllers (e.g. the "HYTE Nexus" detector groups the THICC
        // Q60 and Nexus Portal NP50 into one mislabeled row), so those are skipped
        // below to avoid duplicate, wrongly-typed entries.
        var result = new List<SupportedDeviceDto>(FirstPartyDevices.Count + file.Devices.Count);
        result.AddRange(FirstPartyDevices);

        foreach (var d in file.Devices)
        {
            if ((d.Controller ?? "").StartsWith("HYTE", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var (vendor, model) = SplitVendorModel(d.Name);
            result.Add(new SupportedDeviceDto
            {
                Vendor = vendor,
                Model = model,
                Category = CategoryFromController(d.Controller, d.Kind),
                VendorId = d.Vid ?? "-",
                ProductId = d.Pid ?? "-",
                Capabilities = new List<string> { d.Kind ?? "generic" },
                Source = "openrgb",
            });
        }
        return result;
    }

    /// <summary>
    /// HYTE devices Nexus drives natively (VID 0x3402). Curated so they carry the
    /// correct model, category, and VID/PID independent of how the OpenRGB fork
    /// happens to register them. PIDs from src/Peripherals/Hyte/*.
    /// </summary>
    private static readonly IReadOnlyList<SupportedDeviceDto> FirstPartyDevices = new List<SupportedDeviceDto>
    {
        Hyte("THICC Q60",         "aio",      "0x0400"),
        Hyte("Q80",               "aio",      "0x0403"),
        Hyte("Nexus Portal NP50", "light",    "0x0901"),
        Hyte("CNVS",              "mousemat", "0x0B00"),
        Hyte("Keeb TKL",          "keyboard", "0x0300"),
        Hyte("Smart Hub",         "light",    "0x0904"),
        Hyte("MiniHub",           "light",    "0x0900"),
    };

    private static SupportedDeviceDto Hyte(string model, string category, string pid) => new()
    {
        Vendor = "HYTE",
        Model = model,
        Category = category,
        VendorId = "0x3402",
        ProductId = pid,
        Capabilities = new List<string> { "rgb" },
        Source = "nexus",
    };

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
        if (c.Contains("Case"))
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
