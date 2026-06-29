using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiTl;

// Immutable channel topology published atomically from DiscoverFans. Readers take
// the reference once and index all arrays from that single object, preventing the
// cross-array length race that would occur if ChannelCount and array references
// were read separately from the hub.
public sealed class TlHubSnapshot
{
    // Shared empty sentinel; callers test reference identity or ChannelCount == 0.
    public static readonly TlHubSnapshot Empty =
        new TlHubSnapshot(Array.Empty<int>(), Array.Empty<int>(), new Dictionary<int, int>(0));

    private readonly Dictionary<int, int> _byAddress;

    public TlHubSnapshot(int[] ports, int[] fanIndexes, Dictionary<int, int> byAddress)
    {
        Port = ports;
        FanIndex = fanIndexes;
        _byAddress = byAddress;
        Rpm = new int[ports.Length];
        Duty = new int[ports.Length];
        for (int i = 0; i < Rpm.Length; i++)
        {
            Rpm[i] = -1;
        }
    }

    public int[] Port { get; }
    public int[] FanIndex { get; }

    // Per-element int writes are atomic on all supported platforms; PollRpm and
    // SetSpeed mutate these in place on the current snapshot without a lock.
    public int[] Rpm { get; }
    public int[] Duty { get; }

    public int ChannelCount => Port.Length;

    public int GetChannelIndex(int port, int fanIndex) =>
        _byAddress.TryGetValue(TlFanProtocol.Address(port, fanIndex), out int ch) ? ch : -1;
}
