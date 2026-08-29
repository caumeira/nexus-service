using System.IO;
using System.Text.Json.Nodes;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// The two registration sections the bundled daemon reads for hardware no
/// detector can match: QMK-OpenRGB boards and E1.31 devices. Shapes are checked
/// against what QMKOpenRGBControllerDetect.cpp and E131ControllerDetect.cpp
/// actually parse.
/// </summary>
public class OpenRgbManualDevicesTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-openrgb-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonObject ReadConfig(string dir)
        => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "OpenRGB.json")))!;

    [Fact]
    public void Qmk_entry_lands_in_the_shape_the_detector_parses()
    {
        var dir = NewDir();
        var devices = new OpenRgbManualDevices();
        devices.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "Keychron Q6 Pro", UsbVid = "3434", UsbPid = "0660" });

        Assert.True(OpenRgbManualDeviceConfig.Write(dir, devices));

        var entry = (JsonObject)ReadConfig(dir)["QMKOpenRGBDevices"]!["devices"]![0]!;
        Assert.Equal("Keychron Q6 Pro", (string)entry["name"]!);
        Assert.Equal("3434", (string)entry["usb_vid"]!);
        Assert.Equal("0660", (string)entry["usb_pid"]!);
    }

    /// <summary>The detector parses ids with std::stoi(s, 0, 16), so a 0x prefix would not parse.</summary>
    [Fact]
    public void Hex_ids_are_written_without_a_prefix()
    {
        Assert.Equal("3434", OpenRgbManualDeviceConfig.NormalizeHex("0x3434"));
        Assert.Equal("0660", OpenRgbManualDeviceConfig.NormalizeHex("0x0660"));
        Assert.Equal("A1B2", OpenRgbManualDeviceConfig.NormalizeHex("a1b2"));
    }

    [Fact]
    public void E131_entry_carries_every_field_the_detector_reads()
    {
        var dir = NewDir();
        var devices = new OpenRgbManualDevices();
        devices.E131.Add(new E131DeviceEntry
        {
            Name = "Desk", Ip = "192.168.1.50", NumLeds = 120,
            StartUniverse = 2, StartChannel = 5, KeepaliveTime = 1, UniverseSize = 510,
        });

        OpenRgbManualDeviceConfig.Write(dir, devices);

        var entry = (JsonObject)ReadConfig(dir)["E131Devices"]!["devices"]![0]!;
        Assert.Equal("Desk", (string)entry["name"]!);
        Assert.Equal("192.168.1.50", (string)entry["ip"]!);
        Assert.Equal(120, (int)entry["num_leds"]!);
        Assert.Equal(2, (int)entry["start_universe"]!);
        Assert.Equal(5, (int)entry["start_channel"]!);
        Assert.Equal(1, (int)entry["keepalive_time"]!);
        Assert.Equal(510, (int)entry["universe_size"]!);
    }

    /// <summary>The daemon's own keys must survive; we only own our two sections.</summary>
    [Fact]
    public void Existing_config_keys_are_preserved()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "OpenRGB.json"),
            """{"Detectors":{"detectors":{"Nollie 1CH":false}},"Something":{"kept":1}}""");

        var devices = new OpenRgbManualDevices();
        devices.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "K", UsbVid = "3434", UsbPid = "0660" });
        OpenRgbManualDeviceConfig.Write(dir, devices);

        var root = ReadConfig(dir);
        Assert.False((bool)root["Detectors"]!["detectors"]!["Nollie 1CH"]!);
        Assert.Equal(1, (int)root["Something"]!["kept"]!);
        Assert.NotNull(root["QMKOpenRGBDevices"]);
    }

    [Fact]
    public void Rewriting_the_same_registrations_is_a_no_op()
    {
        var dir = NewDir();
        var devices = new OpenRgbManualDevices();
        devices.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "K", UsbVid = "3434", UsbPid = "0660" });

        Assert.True(OpenRgbManualDeviceConfig.Write(dir, devices));
        Assert.False(OpenRgbManualDeviceConfig.Write(dir, devices));
    }

    [Fact]
    public void Entries_without_ids_or_an_ip_are_skipped()
    {
        var dir = NewDir();
        var devices = new OpenRgbManualDevices();
        devices.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "no ids" });
        devices.E131.Add(new E131DeviceEntry { Name = "no ip" });

        OpenRgbManualDeviceConfig.Write(dir, devices);

        var root = ReadConfig(dir);
        Assert.Empty((JsonArray)root["QMKOpenRGBDevices"]!["devices"]!);
        Assert.Empty((JsonArray)root["E131Devices"]!["devices"]!);
    }

    // ── Import ──

    private static string WriteSource(string json)
    {
        var path = Path.Combine(NewDir(), "OpenRGB.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Import_reads_both_sections_from_a_users_config()
    {
        var path = WriteSource("""
        {
          "QMKOpenRGBDevices": { "devices": [ { "name": "Keychron Q6 Pro", "usb_vid": "3434", "usb_pid": "0660" } ] },
          "E131Devices": { "devices": [ { "name": "Desk", "ip": "10.0.0.5", "num_leds": 60, "start_universe": 3 } ] }
        }
        """);

        var result = OpenRgbConfigImport.Read(path);

        Assert.True(result.Found);
        var qmk = Assert.Single(result.Qmk);
        Assert.Equal("3434", qmk.UsbVid);
        Assert.Equal("0660", qmk.UsbPid);
        var e131 = Assert.Single(result.E131);
        Assert.Equal("10.0.0.5", e131.Ip);
        Assert.Equal(60, e131.NumLeds);
        Assert.Equal(3, e131.StartUniverse);
        // Absent keys take the detector's own defaults.
        Assert.Equal(1, e131.StartChannel);
        Assert.Equal(512, e131.UniverseSize);
    }

    [Fact]
    public void Import_of_a_missing_or_malformed_file_is_empty_not_an_error()
    {
        var missing = OpenRgbConfigImport.Read(Path.Combine(NewDir(), "nope.json"));
        Assert.False(missing.Found);
        Assert.Empty(missing.Qmk);

        var malformed = OpenRgbConfigImport.Read(WriteSource("{ not json"));
        Assert.False(malformed.Found);
    }

    [Fact]
    public void Merge_adds_new_entries_and_never_duplicates()
    {
        var into = new OpenRgbManualDevices();
        into.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "mine", UsbVid = "3434", UsbPid = "0660" });

        var imported = new OpenRgbConfigImport.Result();
        imported.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "theirs", UsbVid = "3434", UsbPid = "0660" });
        imported.Qmk.Add(new QmkOpenRgbDeviceEntry { Name = "new", UsbVid = "AAAA", UsbPid = "BBBB" });
        imported.E131.Add(new E131DeviceEntry { Name = "Desk", Ip = "10.0.0.5", StartUniverse = 1 });

        var added = OpenRgbConfigImport.Merge(into, imported);

        Assert.Equal(2, added);
        Assert.Equal(2, into.Qmk.Count);
        // The user's own name for an already-registered board is not overwritten.
        Assert.Equal("mine", into.Qmk[0].Name);
        Assert.Single(into.E131);

        // A second import of the same source changes nothing.
        Assert.Equal(0, OpenRgbConfigImport.Merge(into, imported));
    }

    [Fact]
    public void Default_source_path_points_at_an_openrgb_config()
    {
        var path = OpenRgbConfigImport.DefaultSourcePath();
        Assert.Contains("OpenRGB", path);
        Assert.EndsWith("OpenRGB.json", path);
    }
}
