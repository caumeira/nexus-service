using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.Nzxt;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Presents a connected Kraken's LCD as a streamed panel. There is no bus to poll: the
/// cooler's own connection worker owns discovery, so this reports whatever the hub
/// currently holds, and absence is a normal tick result.
///
/// The LCD takes whole uncompressed frames, so the profile asks for
/// <see cref="StreamCodec.RawBgra"/> and the overlay skips its H.264 encoder.
/// </summary>
public sealed class KrakenPanelDiscovery : IStreamedPanelDiscovery
{
    /// <summary>Persisted per-serial override key; changing it resets user overrides.</summary>
    private const string ProfileKind = "kraken-round";

    private readonly KrakenHub _hub;

    public KrakenPanelDiscovery(KrakenHub hub)
    {
        _hub = hub;
    }

    public string HandlerId => KrakenHub.DeviceId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        // The bulk pipe is what carries frames; without it the cooler still works as a
        // cooler but has no panel to offer.
        if (!_hub.IsConnected || !_hub.HasLcd)
        {
            return Array.Empty<StreamedPanelDeviceInfo>();
        }
        return new[]
        {
            new StreamedPanelDeviceInfo
            {
                Serial = _hub.Serial ?? KrakenHub.DeviceId,
                Profile = new StreamedPanelProfile
                {
                    Kind = ProfileKind,
                    DisplayName = "NZXT Kraken LCD",
                    Surface = PanelSurfaces.Kraken,
                    CssWidth = KrakenProtocol.LcdWidth,
                    CssHeight = KrakenProtocol.LcdHeight,
                    Dpr = 1.0,
                    // The device is the ceiling at ~2.3 fps: a 1.6 MB frame over a pipe
                    // that accepts 4.0 MB/s however it is chunked (measured 64 KiB to
                    // 2 MiB, all 4.0). Asking for 3 keeps the pipe saturated and lets the
                    // queue trim, so the panel shows the freshest frame the device can
                    // take rather than idling between paced sends.
                    Fps = 3,
                    Codec = StreamCodec.RawBgra,
                },
            },
        };
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new KrakenStreamTransport(_hub, info.Serial);
}
