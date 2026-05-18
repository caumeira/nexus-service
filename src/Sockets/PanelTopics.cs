using System;
using Qos.Service.Models.Panel;
using Qos.Service.Serialization;

namespace Qos.Service.Sockets;

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
    public const string PanelDevice = "panel/device";
    /// <summary>
    /// Manual pair-code lifecycle. Dashboard subscribes while the Pair
    /// Remote sheet is open; payload kinds are "request" (phone submitted
    /// the active code) and "cancelled" (expired / denied / superseded).
    /// </summary>
    public const string PairCodeRequest = "panel/phone/pair-code/request";

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
        var env = WsEnvelope.Build(PairCodeRequest, frame, AppJsonContext.Default.PanelPhonePairCodeRequestFrame);
        _ = hub.BroadcastTopicAsync(PairCodeRequest, env);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
