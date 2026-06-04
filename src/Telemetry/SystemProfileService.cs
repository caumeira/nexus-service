using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Attaches a system profile to the anonymous telemetry person (PostHog
/// <c>$set</c>): the same hardware shown under System Specs (cpu / gpu / ram /
/// motherboard / storage / …) plus the recognized devices currently connected
/// — both the Nexus/HYTE devices we drive AND named USB peripherals we merely
/// identify, in a single <c>devices</c> list. No PII (no serials, no machine
/// name).
///
/// Lifecycle: warms up (30 s cadence) until the first ready snapshot lands —
/// device connection + USB enumeration aren't instant at boot — then settles to
/// a 15-min re-check. Each send is deduped: a <c>$identify</c> only goes out
/// when the profile actually changed, so unchanged refreshes cost zero events.
/// Gated by the same opt-out — nothing is gathered or sent while opted out.
/// </summary>
internal sealed class SystemProfileService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromMinutes(15);
    private const int MaxDevices = 80;

    private readonly ITelemetry _telemetry;
    private readonly IConfigStore _store;
    private readonly SystemSpecsCollector _specs;
    private readonly DeviceManager _devices;
    private string? _lastSignature;

    public SystemProfileService(ITelemetry telemetry, IConfigStore store,
        SystemSpecsCollector specs, DeviceManager devices)
    {
        _telemetry = telemetry;
        _store = store;
        _specs = specs;
        _devices = devices;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            bool settled;
            try { settled = await SendAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[profile] {ex.GetType().Name}: {ex.Message}");
                settled = false;
            }
            // Keep the fast cadence until a ready snapshot lands; then back off.
            delay = settled ? SteadyInterval : WarmupInterval;
        }
    }

    /// <returns>True once "settled" — a ready snapshot was processed, or the user
    /// opted out — so the caller can drop to the slow cadence. False means
    /// not-ready-yet; retry soon.</returns>
    private async Task<bool> SendAsync(CancellationToken ct)
    {
        if (!_store.Load().Telemetry.CollectAnonymousData)
            return true; // opted out — re-check slowly, don't gather.

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);

        // One list: handler-backed devices we drive (connected) ∪ any USB device
        // the OS names (recognized even if unsupported).
        var devices = _devices.GetAll()
            .Where(d => d.Connected)
            .Select(d => d.Name)
            .Concat(_devices.GetUsbDevices().Select(u => u.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Stable order so a reordered enumeration doesn't look like a change
            // (otherwise the dedupe re-sends $identify every refresh).
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDevices)
            .ToArray();

        // Devices + enumeration aren't ready instantly at boot. Don't send an
        // empty snapshot — wait for the next warmup tick.
        if (string.IsNullOrWhiteSpace(specs.Processor) && devices.Length == 0)
            return false;

        var props = new List<(string, object?)> { ("version", BuildInfo.Version), ("os", OsTag()) };
        Add(props, "os_build", specs.OsBuild);
        Add(props, "cpu", specs.Processor);
        Add(props, "gpu", specs.GraphicsCard);
        Add(props, "ram", specs.Memory);
        if (ParseRamGb(specs.Memory) is int ramGb) props.Add(("ram_amount", ramGb));
        Add(props, "motherboard", specs.Motherboard);
        Add(props, "storage", specs.Storage);
        if (ParseStorageGb(specs.Storage) is int storageGb) props.Add(("storage_amount", storageGb));
        Add(props, "monitor", specs.Monitor);
        Add(props, "network", specs.NetworkCard);
        Add(props, "sound", specs.SoundCard);
        props.Add(("devices", devices));

        // Dedupe: only emit when the profile actually changed.
        var signature = string.Join("|", props.Select(p => p.Item1 + "=" + Stringify(p.Item2)));
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _telemetry.Identify(props.ToArray());
        }
        return true;
    }

    private static string Stringify(object? value) => value switch
    {
        null => "",
        // Unit-separator delimiter so a device name containing the delimiter
        // can't collide two entries into one (a missed change).
        string[] a => string.Join('\u001f', a),
        _ => value.ToString() ?? "",
    };

    private static void Add(List<(string, object?)> props, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            props.Add((key, value.Trim()));
    }

    // "32 GB DDR5-6000 (...)" -> 32. The first GB figure is the total.
    internal static int? ParseRamGb(string memory)
    {
        var m = Regex.Match(memory, @"(\d+(?:\.\d+)?)\s*GB", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v)
            : null;
    }

    // Sum the capacity of each drive in "1.82 TB Model + 931 GB Model" (reported
    // in GiB to match the storage string). Each drive segment leads with its
    // capacity, so take only the FIRST figure per segment — a model name that
    // embeds a size (e.g. "WD Blue SN570 1TB") must not be double-counted.
    internal static int? ParseStorageGb(string storage)
    {
        double total = 0;
        var any = false;
        foreach (var drive in storage.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(drive, @"(\d+(?:\.\d+)?)\s*(TB|GB)", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                total += m.Groups[2].Value.Equals("TB", StringComparison.OrdinalIgnoreCase) ? v * 1024 : v;
                any = true;
            }
        }
        return any ? (int)Math.Round(total) : null;
    }

    private static string OsTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "mac";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "other";
    }
}
