using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals.Protocols.Razer;

/// <summary>
/// Razer mouse profile table. Data lives in protocols/razer-mouse.json.
/// Loads the embedded resource once at startup and exposes a PID -> profile dict.
///
/// To add a device: edit the JSON file and rebuild.
/// </summary>
internal static class RazerMouseProfiles
{
    private static readonly Lazy<Dictionary<int, RazerMouseProfile>> _byPid = new(Load);

    public static IReadOnlyDictionary<int, RazerMouseProfile> ByPid => _byPid.Value;

    private static Dictionary<int, RazerMouseProfile> Load()
    {
        var asm = typeof(RazerMouseProfiles).Assembly;
        using var stream = asm.GetManifestResourceStream("razer-mouse.json");
        if (stream is null)
        {
            Console.Error.WriteLine("[razer] razer-mouse.json resource not found; profile table empty");
            return new Dictionary<int, RazerMouseProfile>();
        }

        RazerMouseSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize(stream, AppJsonContext.Default.RazerMouseSpec);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[razer] failed to parse razer-mouse.json: {ex.Message}");
            return new Dictionary<int, RazerMouseProfile>();
        }

        if (spec?.Devices is null)
        {
            return new Dictionary<int, RazerMouseProfile>();
        }

        var dict = new Dictionary<int, RazerMouseProfile>(spec.Devices.Count);
        foreach (var d in spec.Devices)
        {
            if (!TryParseHex(d.Pid, out var pid))
            {
                continue;
            }
            if (!TryParseHex(d.TransactionId, out var txid))
            {
                continue;
            }

            var variant = d.Polling?.Equals("hyperpolling", StringComparison.OrdinalIgnoreCase) == true
                ? RazerPollingVariant.HyperPolling
                : RazerPollingVariant.Standard;

            dict[pid] = new RazerMouseProfile(
                Name: d.Name ?? "",
                TransactionId: (byte)txid,
                MaxDpi: d.MaxDpi,
                PollingVariant: variant,
                HasBattery: d.HasBattery,
                HasSleep: d.HasSleep);
        }
        return dict;
    }

    private static bool TryParseHex(string? input, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }
        var s = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? input.Substring(2) : input;
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

// --- JSON shape ---

public sealed class RazerMouseSpec
{
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("vendorId")] public string? VendorId { get; set; }
    [JsonPropertyName("devices")] public List<RazerMouseSpecEntry> Devices { get; set; } = new();
}

public sealed class RazerMouseSpecEntry
{
    [JsonPropertyName("pid")] public string? Pid { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
    [JsonPropertyName("maxDpi")] public int MaxDpi { get; set; }
    [JsonPropertyName("polling")] public string? Polling { get; set; }
    [JsonPropertyName("hasBattery")] public bool HasBattery { get; set; }
    [JsonPropertyName("hasSleep")] public bool HasSleep { get; set; }
}
