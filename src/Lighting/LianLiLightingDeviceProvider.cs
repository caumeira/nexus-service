using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the Lian Li Uni Hub SL-Infinity fans as drivable
/// <see cref="LightingDevice"/>s, two per active port (inner-ring and outer-ring
/// channels). Fan counts come from persisted <see cref="LianLiSettings"/>;
/// the frame writer streams at 30 Hz.
/// </summary>
public sealed class LianLiLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public LianLiLightingDeviceProvider(LianLiHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the connection worker each poll tick and by the fan-count route.
    /// Fires <see cref="DevicesChanged"/> only when the lit-device topology
    /// changes (connect/disconnect, or a per-port fan count edit alters the zone
    /// LED counts), so the bridge rebuilds frame mappings without churning on
    /// every RPM tick.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        var lianLi = _store.Load().Devices.LianLi;
        var sb = new System.Text.StringBuilder("connected");
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            sb.Append('|').Append(ClampFans(lianLi.GetFans(p)));
        }
        return sb.ToString();
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;

        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var lianLi = settings.Devices.LianLi;

        var slot = 0;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            var fans = ClampFans(lianLi.GetFans(p));
            if (fans <= 0) continue;

            var innerLeds = fans * LianLiProtocol.InnerLedsPerFan;
            var outerLeds = fans * LianLiProtocol.OuterLedsPerFan;

            resp.Devices.Add(BuildZone(
                id: $"{hubId}:port{p}:inner",
                name: $"Lian Li - Port {p} Inner Ring",
                firmwareLedCount: innerLeds,
                zoneIndex: slot++,
                parentDeviceId: hubId,
                deviceKey: DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"port{p}inner"),
                disabled, prefs, layouts));

            resp.Devices.Add(BuildZone(
                id: $"{hubId}:port{p}:outer",
                name: $"Lian Li - Port {p} Outer Ring",
                firmwareLedCount: outerLeds,
                zoneIndex: slot++,
                parentDeviceId: hubId,
                deviceKey: DeviceKeyComputer.ForFirstParty(LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"port{p}outer"),
                disabled, prefs, layouts));
        }
        return resp;
    }

    // Persisted fan count is bounded to MaxFansPerPort on the write path; a
    // hand-edited settings file could exceed it, overflowing the frame writer's
    // fixed per-channel buffer. Clamp at read so derived LED counts stay bounded.
    private static int ClampFans(int fans) => System.Math.Min(fans, LianLiProtocol.MaxFansPerPort);

    private static LightingDevice BuildZone(
        string id, string name, int firmwareLedCount, int zoneIndex, string parentDeviceId,
        string deviceKey,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == id)
            {
                isOn = false;
                break;
            }
        }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness;
            hue = pref.Hue;
            saturation = pref.Saturation;
        }
        var (defX, defY, defW, defH) = DefaultLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            DeviceKey = deviceKey,
            Name = name,
            Type = "ledstrip",
            IconType = "fan",
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = firmwareLedCount,
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId,
            ZoneIndex = zoneIndex,
            ZoneType = "linear",
            ZoneResizable = false,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id)) return;
            var next = new List<string>(current.Count);
            foreach (var x in current)
            {
                if (x != id)
                {
                    next.Add(x);
                }
            }
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id)) return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0) return;
        _store.Update(s => s.Devices.ZoneLedCounts[id] = count);
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
        {
            return Array.Empty<DeviceStructure>();
        }
        var hubId = _hub.DeviceId;
        var lianLi = _store.Load().Devices.LianLi;
        var structures = new List<DeviceStructure>();

        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            var fans = ClampFans(lianLi.GetFans(p));
            if (fans <= 0) continue;

            AddZoneStructure(structures, hubId, p, "inner",
                $"Lian Li - Port {p} Inner Ring", $"Port {p} Inner Ring",
                fans * LianLiProtocol.InnerLedsPerFan, fans);

            AddZoneStructure(structures, hubId, p, "outer",
                $"Lian Li - Port {p} Outer Ring", $"Port {p} Outer Ring",
                fans * LianLiProtocol.OuterLedsPerFan, fans);
        }
        return structures;
    }

    private static void AddZoneStructure(
        List<DeviceStructure> list, string hubId, int port, string channel,
        string name, string rawName, int ledCount, int fans)
    {
        var id = $"{hubId}:port{port}:{channel}";
        var key = DeviceKeyComputer.ForFirstParty(
            LianLiProtocol.VendorId, LianLiProtocol.ProductId, $"port{port}{channel}");
        var (defaultU, defaultV) = BuildFanClumpUV(fans);
        var structure = new DeviceStructure
        {
            DeviceId = id,
            Name = name,
            DeviceKey = key,
        };
        // One ring per fan; fans laid out side by side along u.
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = defaultU,
            DefaultV = defaultV,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        list.Add(structure);
    }

    private static (float[] u, float[] v) BuildFanClumpUV(int fans)
    {
        var ledCount = fans * LianLiProtocol.LedsPerFanPerChannel;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var f = 0; f < fans; f++)
        {
            var centerU = (f + 0.5f) / fans;
            for (var i = 0; i < LianLiProtocol.LedsPerFanPerChannel; i++)
            {
                var angle = (i / (double)LianLiProtocol.LedsPerFanPerChannel) * 2.0 * Math.PI;
                u[f * LianLiProtocol.LedsPerFanPerChannel + i] =
                    centerU + (0.40f / fans) * (float)Math.Cos(angle);
                v[f * LianLiProtocol.LedsPerFanPerChannel + i] =
                    0.5f + 0.40f * (float)Math.Sin(angle);
            }
        }
        return (u, v);
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();

        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var lianLi = settings.Devices.LianLi;

        var slot = 0;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            var fans = ClampFans(lianLi.GetFans(p));
            if (fans <= 0) continue;

            frames.Add(BuildOrReuseFrame(
                $"{hubId}:port{p}:inner", fans * LianLiProtocol.InnerLedsPerFan,
                slot++, layouts, ref idx));

            frames.Add(BuildOrReuseFrame(
                $"{hubId}:port{p}:outer", fans * LianLiProtocol.OuterLedsPerFan,
                slot++, layouts, ref idx));
        }

        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames)
            {
                live.Add(f.Id);
            }
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys)
            {
                if (!live.Contains(k))
                {
                    stale.Add(k);
                }
            }
            foreach (var k in stale)
            {
                _frameCache.Remove(k);
            }
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseFrame(
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == firmwareLedCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }

        var frame = new DeviceFrame(
            index: thisIdx,
            id: id,
            ledCount: firmwareLedCount,
            x: layout?.X ?? defX,
            y: layout?.Y ?? defY,
            w: layout?.W ?? defW,
            h: layout?.H ?? defH,
            rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float Y = 540f;
        const float W = 200f;
        const float H = 30f;
        const float Gap = 220f;
        const float BaseX = 40f;
        const int Cols = 4;
        var col = slot % Cols;
        var row = slot / Cols;
        return (BaseX + col * Gap, Y + row * (H + 10f), W, H);
    }
}
