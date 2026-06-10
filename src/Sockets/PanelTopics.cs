using System;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sockets;

/// <summary>
/// Small fan-out helpers for the four panel-related multiplex topics.
/// Routes call these at the end of any mutation; subscribers refetch the
/// canonical resource on receive (no payload diffing).
/// </summary>
public static class PanelTopics
{
    public const string Prefs = "prefs";
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Volume = "volume";
    public const string CoolingWarnings = "cooling/warnings";
    public const string PanelDevice = "panel/device";
    /// <summary>
    /// Display topology or monitor-panel assignment changed. Subscribers
    /// refetch GET /displays/topology.
    /// </summary>
    public const string Displays = "displays";
    /// <summary>
    /// Manual pair-code lifecycle. Dashboard subscribes while the Pair
    /// Remote sheet is open; payload kinds are "request" (phone submitted
    /// the active code) and "cancelled" (expired / denied / superseded).
    /// </summary>
    public const string PairCodeRequest = "panel/phone/pair-code/request";
    /// <summary>
    /// Host network address changed (VPN toggle, Wi-Fi↔wired switch, DHCP
    /// renew). A displayed pairing QR embeds the LAN IP picked at mint time, so
    /// subscribers re-fetch the QR for the current address instead of waiting
    /// out its TTL.
    /// </summary>
    public const string PairQrRefresh = "panel/phone/pair-qr/refresh";

    public static void BroadcastPrefs(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Prefs))
            return;
        var frame = new PrefsChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Prefs, frame, AppJsonContext.Default.PrefsChangedFrame);
        _ = hub.BroadcastTopicAsync(Prefs, env);
    }

    public static void BroadcastLighting(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Lighting))
            return;
        var frame = new LightingChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Lighting, frame, AppJsonContext.Default.LightingChangedFrame);
        _ = hub.BroadcastTopicAsync(Lighting, env);
    }

    public static void BroadcastCooling(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Cooling))
            return;
        var frame = new CoolingChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Cooling, frame, AppJsonContext.Default.CoolingChangedFrame);
        _ = hub.BroadcastTopicAsync(Cooling, env);
    }

    public static void BroadcastVolume(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Volume))
            return;
        var frame = new VolumeChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Volume, frame, AppJsonContext.Default.VolumeChangedFrame);
        _ = hub.BroadcastTopicAsync(Volume, env);
    }

    /// <summary>
    /// Broadcast a cooling-warnings-changed notification. Callers (the NP50
    /// heartbeat worker and any future warning producers) invoke this when
    /// the active warning set transitions. Subscribers refetch
    /// <c>GET /cooling/warnings</c>; <paramref name="deviceId"/> lets the UI
    /// scope which device's warnings to re-render.
    /// </summary>
    public static void BroadcastCoolingWarnings(MultiplexHub hub, string deviceId)
    {
        if (!hub.TopicHasSubscribers(CoolingWarnings))
            return;
        var frame = new CoolingWarningsChangedFrame { Revision = Now(), DeviceId = deviceId };
        var env = WsEnvelope.Build(CoolingWarnings, frame, AppJsonContext.Default.CoolingWarningsChangedFrame);
        _ = hub.BroadcastTopicAsync(CoolingWarnings, env);
    }

    public static void BroadcastPairQrRefresh(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(PairQrRefresh))
            return;
        var frame = new PairQrRefreshFrame { Revision = Now() };
        var env = WsEnvelope.Build(PairQrRefresh, frame, AppJsonContext.Default.PairQrRefreshFrame);
        _ = hub.BroadcastTopicAsync(PairQrRefresh, env);
    }

    public static void BroadcastDisplays(MultiplexHub hub)
    {
        if (!hub.TopicHasSubscribers(Displays))
            return;
        var frame = new Models.Displays.DisplaysChangedFrame { Revision = Now() };
        var env = WsEnvelope.Build(Displays, frame, AppJsonContext.Default.DisplaysChangedFrame);
        _ = hub.BroadcastTopicAsync(Displays, env);
    }

    public static void BroadcastPanelDevice(MultiplexHub hub, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !hub.TopicHasSubscribers(PanelDevice))
            return;
        var frame = new PanelDeviceChangedFrame { Revision = Now(), DeviceId = deviceId };
        var env = WsEnvelope.Build(PanelDevice, frame, AppJsonContext.Default.PanelDeviceChangedFrame);
        _ = hub.BroadcastTopicAsync(PanelDevice, env);
    }

    public static void BroadcastPairCodeRequest(MultiplexHub hub, PanelPhonePairCodeRequestFrame frame)
    {
        if (!hub.TopicHasSubscribers(PairCodeRequest))
            return;
        _ = hub.BroadcastTopicAsync(PairCodeRequest, BuildPairCodeRequestEnvelope(frame));
    }

    /// <summary>
    /// Build the wire envelope for a pair-code/request frame. Shared by the
    /// live broadcast above and the snapshot provider in
    /// <see cref="Nexus.Service.Panel.PanelPhonePairingService"/>, which
    /// replays the currently-pending request to a dashboard that connects
    /// mid-handshake (e.g. one opened from the tray pairing notification
    /// after the one-shot live broadcast already fired).
    /// </summary>
    public static ReadOnlyMemory<byte> BuildPairCodeRequestEnvelope(PanelPhonePairCodeRequestFrame frame)
        => WsEnvelope.Build(PairCodeRequest, frame, AppJsonContext.Default.PanelPhonePairCodeRequestFrame);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
