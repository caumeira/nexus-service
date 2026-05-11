using System.Collections.Generic;
using Qos.Service.Models.Peripherals;

namespace Qos.Service.Peripherals;

/// <summary>
/// Hand-curated list of peripherals Qos knows about. Users search this to
/// answer "is my device supported?". The modal highlights rows whose VID/PID
/// matches a currently-plugged-in device via the raw USB enumerator.
/// </summary>
public static class SupportedDevicesCatalog
{
    public static readonly IReadOnlyList<SupportedDeviceDto> All = new List<SupportedDeviceDto>
    {
        // Razer mice — all entries here are protocol-supported via RazerMousePeripheral
        // + RazerMouseProfiles. To add a new device: put a row in RazerMouseProfiles.ByPid
        // and another here with matching VID/PID/capabilities. Caps reflect what the profile
        // advertises (battery + sleep only for wireless models).
        Mouse("Razer", "DeathAdder V2 Pro (Wired)",      "0x1532", "0x007C", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V2 Pro",              "0x1532", "0x007D", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "DeathAdder V2",                  "0x1532", "0x0084", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V2 Mini",             "0x1532", "0x008C", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V2 Lite",             "0x1532", "0x00A1", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder Essential (2021)",    "0x1532", "0x0098", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder Elite",               "0x1532", "0x005C", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V3",                  "0x1532", "0x00B2", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V3 Pro (Wired)",      "0x1532", "0x00B6", Caps("dpi", "polling")),
        Mouse("Razer", "DeathAdder V3 Pro",              "0x1532", "0x00B7", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "DeathAdder V3 HyperSpeed",       "0x1532", "0x00C5", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "DeathAdder V4 Pro",              "0x1532", "0x00BF", Caps("dpi", "polling", "battery", "sleep")),

        Mouse("Razer", "Viper",                          "0x1532", "0x0078", Caps("dpi", "polling")),
        Mouse("Razer", "Viper Ultimate",                 "0x1532", "0x007B", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Viper Mini",                     "0x1532", "0x008A", Caps("dpi", "polling")),
        Mouse("Razer", "Viper Mini SE",                  "0x1532", "0x009F", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Viper 8K",                       "0x1532", "0x0091", Caps("dpi", "polling")),
        Mouse("Razer", "Viper V2 Pro",                   "0x1532", "0x00A6", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Viper V3 HyperSpeed",            "0x1532", "0x00B8", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Viper V3 Pro",                   "0x1532", "0x00C1", Caps("dpi", "polling", "battery", "sleep")),

        Mouse("Razer", "Basilisk",                       "0x1532", "0x0064", Caps("dpi", "polling")),
        Mouse("Razer", "Basilisk V2",                    "0x1532", "0x0085", Caps("dpi", "polling")),
        Mouse("Razer", "Basilisk Ultimate",              "0x1532", "0x0088", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Basilisk V3",                    "0x1532", "0x0099", Caps("dpi", "polling")),
        Mouse("Razer", "Basilisk V3 Pro",                "0x1532", "0x00AB", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Basilisk V3 35K",                "0x1532", "0x00CB", Caps("dpi", "polling")),
        Mouse("Razer", "Basilisk V3 Pro 35K",            "0x1532", "0x00CD", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Basilisk V3 X HyperSpeed",       "0x1532", "0x00B9", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Basilisk X HyperSpeed",          "0x1532", "0x0083", Caps("dpi", "polling", "battery", "sleep")),

        Mouse("Razer", "Naga Pro",                       "0x1532", "0x0090", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Naga V2 Pro",                    "0x1532", "0x00A8", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Naga Trinity",                   "0x1532", "0x0067", Caps("dpi", "polling")),

        Mouse("Razer", "Orochi V2",                      "0x1532", "0x0094", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Cobra",                          "0x1532", "0x00A3", Caps("dpi", "polling")),
        Mouse("Razer", "Cobra Pro",                      "0x1532", "0x00B0", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Pro Click",                      "0x1532", "0x0077", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Pro Click Mini",                 "0x1532", "0x009A", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Lancehead",                      "0x1532", "0x005A", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Mamba Wireless",                 "0x1532", "0x0072", Caps("dpi", "polling", "battery", "sleep")),
        Mouse("Razer", "Mamba Elite",                    "0x1532", "0x006C", Caps("dpi", "polling")),
        Mouse("Razer", "Atheris",                        "0x1532", "0x0062", Caps("dpi", "polling", "battery", "sleep")),

        // Razer keyboards
        Keyboard("Razer", "BlackWidow V4",               "0x1532", "0x0293", Caps("gameMode", "fnLock")),
        Keyboard("Razer", "BlackWidow V3",               "0x1532", "0x024E", Caps("gameMode", "fnLock")),
        Keyboard("Razer", "Huntsman V2",                 "0x1532", "0x026C", Caps("gameMode", "fnLock")),
        Keyboard("Razer", "Huntsman V3 Pro",             "0x1532", "0x02A6", Caps("gameMode", "fnLock")),
        Keyboard("Razer", "Ornata V3",                   "0x1532", "0x0294", Caps("gameMode", "fnLock")),

        // Logitech mice
        Mouse("Logitech", "MX Master 3S",                "0x046D", "0xB034", Caps("dpi", "polling", "battery", "smoothScroll", "gestures")),
        Mouse("Logitech", "MX Master 3",                 "0x046D", "0x4082", Caps("dpi", "polling", "battery", "smoothScroll", "gestures")),
        Mouse("Logitech", "MX Anywhere 3",               "0x046D", "0x4090", Caps("dpi", "battery")),
        Mouse("Logitech", "G Pro X Superlight",          "0x046D", "0xC094", Caps("dpi", "polling", "battery")),
        Mouse("Logitech", "G Pro Wireless",              "0x046D", "0xC088", Caps("dpi", "polling", "battery")),
        Mouse("Logitech", "G502 X Plus",                 "0x046D", "0xC097", Caps("dpi", "polling", "battery")),
        Mouse("Logitech", "G502 HERO",                   "0x046D", "0xC08B", Caps("dpi", "polling")),
        Mouse("Logitech", "G703",                        "0x046D", "0xC090", Caps("dpi", "polling", "battery")),

        // Logitech keyboards
        Keyboard("Logitech", "MX Keys",                  "0x046D", "0xB35B", Caps("battery", "fnLock")),
        Keyboard("Logitech", "MX Keys S",                "0x046D", "0xB376", Caps("battery", "fnLock")),
        Keyboard("Logitech", "G915",                     "0x046D", "0xC33E", Caps("battery", "gameMode")),
        Keyboard("Logitech", "G815",                     "0x046D", "0xC33F", Caps("gameMode")),

        // Corsair mice
        Mouse("Corsair", "M65 Pro RGB",                  "0x1B1C", "0x1B2E", Caps("dpi", "polling")),
        Mouse("Corsair", "M65 RGB Elite",                "0x1B1C", "0x1B5A", Caps("dpi", "polling")),
        Mouse("Corsair", "Dark Core RGB Pro",            "0x1B1C", "0x1B4C", Caps("dpi", "polling", "battery")),
        Mouse("Corsair", "Scimitar RGB Elite",           "0x1B1C", "0x1B3E", Caps("dpi", "polling")),
        Mouse("Corsair", "Sabre RGB Pro Wireless",       "0x1B1C", "0x1B8C", Caps("dpi", "polling", "battery")),
        Mouse("Corsair", "Harpoon RGB Wireless",         "0x1B1C", "0x1B6E", Caps("dpi", "polling", "battery")),

        // Corsair keyboards
        Keyboard("Corsair", "K70 RGB Pro",               "0x1B1C", "0x1B6D", Caps("gameMode")),
        Keyboard("Corsair", "K70 RGB MK.2",              "0x1B1C", "0x1B49", Caps("gameMode")),
        Keyboard("Corsair", "K95 RGB Platinum",          "0x1B1C", "0x1B2D", Caps("gameMode")),
        Keyboard("Corsair", "K100 RGB",                  "0x1B1C", "0x1B7D", Caps("gameMode")),

        // SteelSeries mice
        Mouse("SteelSeries", "Rival 600",                "0x1038", "0x1724", Caps("dpi", "polling")),
        Mouse("SteelSeries", "Rival 650",                "0x1038", "0x172B", Caps("dpi", "polling", "battery")),
        Mouse("SteelSeries", "Aerox 3 Wireless",         "0x1038", "0x1830", Caps("dpi", "polling", "battery")),
        Mouse("SteelSeries", "Aerox 5",                  "0x1038", "0x1854", Caps("dpi", "polling")),
        Mouse("SteelSeries", "Prime Wireless",           "0x1038", "0x1842", Caps("dpi", "polling", "battery")),

        // SteelSeries keyboards
        Keyboard("SteelSeries", "Apex Pro",              "0x1038", "0x1610", Caps("gameMode")),
        Keyboard("SteelSeries", "Apex 7",                "0x1038", "0x1612", Caps("gameMode")),

        // SteelSeries headsets
        Headset("SteelSeries", "Arctis Pro Wireless",    "0x1038", "0x1290", Caps("battery", "sidetone")),
        Headset("SteelSeries", "Arctis 7",               "0x1038", "0x1260", Caps("battery", "sidetone")),
        Headset("SteelSeries", "Arctis Nova Pro Wireless","0x1038", "0x12E0", Caps("battery", "sidetone")),

        // Logitech G headsets
        Headset("Logitech", "G Pro X Wireless",          "0x046D", "0x0AB5", Caps("battery", "sidetone")),
        Headset("Logitech", "G733",                      "0x046D", "0x0AB0", Caps("battery")),

        // Razer headsets
        Headset("Razer", "BlackShark V2 Pro",            "0x1532", "0x0531", Caps("battery")),
        Headset("Razer", "Kraken V3 Pro",                "0x1532", "0x0554", Caps("battery")),

        // HyperX
        Mouse("HyperX", "Pulsefire Haste",               "0x03F0", "0x038F", Caps("dpi", "polling")),
        Headset("HyperX", "Cloud II Wireless",           "0x03F0", "0x018B", Caps("battery")),
    };

    private static SupportedDeviceDto Mouse(string vendor, string model, string vid, string pid, List<string> caps) =>
        new() { Vendor = vendor, Model = model, Category = "mouse", VendorId = vid, ProductId = pid, Capabilities = caps };

    private static SupportedDeviceDto Keyboard(string vendor, string model, string vid, string pid, List<string> caps) =>
        new() { Vendor = vendor, Model = model, Category = "keyboard", VendorId = vid, ProductId = pid, Capabilities = caps };

    private static SupportedDeviceDto Headset(string vendor, string model, string vid, string pid, List<string> caps) =>
        new() { Vendor = vendor, Model = model, Category = "headset", VendorId = vid, ProductId = pid, Capabilities = caps };

    private static List<string> Caps(params string[] values) => new(values);
}
