using System;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Hardware-agnostic contract for one physical (or simulated) Stream Deck.
/// <see cref="HidStreamDeckSurface"/> and <see cref="SimulatedStreamDeckSurface"/>
/// both implement this so <see cref="StreamDeckConnectionWorker"/> never branches
/// on transport.
/// </summary>
public interface IStreamDeckSurface : IDisposable
{
    StreamDeckModel Model { get; }
    string Serial { get; }
    string FirmwareVersion { get; }
    bool IsConnected { get; }

    /// <summary>Sets display brightness, 0-100 (clamped).</summary>
    bool SetBrightness(int percent);

    /// <summary>
    /// Pushes already-encoded wire bytes (BMP for gen1) to one key. keyIndex is
    /// the canonical (user-facing, left-to-right/top-to-bottom) index; the
    /// surface applies the model's hardware remap internally.
    /// </summary>
    bool SetKeyImage(int keyIndex, ReadOnlyMemory<byte> wireBytes);

    /// <summary>Blanks one key. No-op (returns false) for models with no known blank image.</summary>
    bool ClearKey(int keyIndex);

    bool Reset();

    /// <summary>
    /// Reads and decodes the next input report, honoring the model's key-index
    /// remap. Returns a snapshot of every key's pressed state (canonical order,
    /// length == Model.KeyCount) on a report, or null on idle/timeout. Flips
    /// IsConnected false when the device is gone.
    /// </summary>
    bool[]? ReadInput(int timeoutMs);
}
