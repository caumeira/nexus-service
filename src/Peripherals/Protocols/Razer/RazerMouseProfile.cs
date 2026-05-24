namespace Nexus.Service.Peripherals.Protocols.Razer;

/// <summary>
/// How a given Razer mouse implements polling-rate commands. Older generations use a
/// single-byte encoding (125/500/1000 Hz); HyperPolling mice use a different command
/// class/id and a 2-byte rate argument supporting up to 8000 Hz.
/// </summary>
public enum RazerPollingVariant
{
    /// <summary>Commands 0x00/0x05 (set) and 0x00/0x85 (get). Rates: 125, 500, 1000 Hz.</summary>
    Standard,
    /// <summary>Commands 0x00/0x40 (set) and 0x00/0xC0 (get). Rates up to 8000 Hz.</summary>
    HyperPolling,
}

/// <summary>
/// Per-device configuration for Razer mice. All the constants OpenRazer's drivers
/// switch on (transaction_id, max DPI, polling support, wireless capabilities) live
/// here so a single <see cref="RazerMousePeripheral"/> class handles every mouse
/// via table lookup.
/// </summary>
public sealed record RazerMouseProfile(
    string Name,
    byte TransactionId,
    int MaxDpi,
    RazerPollingVariant PollingVariant,
    bool HasBattery,
    bool HasSleep);
