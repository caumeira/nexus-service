using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.SmartLights;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Brand-neutral provider for every network ("smart") light. Implements the
/// engine-facing <see cref="ILightingDeviceProvider"/> (control) and
/// <see cref="ILightingFrameContributor"/> (canvas frames) once for all brands,
/// dispatching to the owning <see cref="ILightDriver"/> by id prefix. Control +
/// effect streaming both flow through <see cref="NetworkSendThrottle"/> so a
/// device is never sent faster than its rate ceiling and a slow bridge never
/// stalls the engine. Mirrors the CNVS/NP50 provider pattern; the difference is
/// the transport is the LAN, not a serial/HID hub.
/// </summary>
public sealed class SmartLightProvider : ILightingDeviceProvider, ILightingFrameContributor
{
    private const string IconType = "bulb";

    private readonly Dictionary<string, ILightDriver> _drivers;
    private readonly IConfigStore _store;
    private readonly NetworkSendThrottle _throttle;

    // id -> reachability (optimistic; a failed send flips it, a success restores).
    private readonly ConcurrentDictionary<string, bool> _online = new();
    // id -> live snapshot, refreshed each BuildFrames so the 33 Hz writer's
    // SubmitFrame lookups don't re-parse settings per device per tick.
    private readonly ConcurrentDictionary<string, SmartLight> _cache = new();
    // id -> frame plan captured in BuildFrames so SubmitEffectFrame knows
    // whether the device takes per-zone colors without re-asking the driver.
    private readonly ConcurrentDictionary<string, LightFramePlan> _plans = new();
    // id -> reused DeviceFrame so the per-LED buffer survives RgbBridge rebuilds.
    private readonly Dictionary<string, DeviceFrame> _frames = new();

    public event Action? DevicesChanged;

    public SmartLightProvider(IEnumerable<ILightDriver> drivers, IConfigStore store, NetworkSendThrottle throttle)
    {
        _drivers = new Dictionary<string, ILightDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drivers) _drivers[d.Brand] = d;
        _store = store;
        _throttle = throttle;
    }

    public bool IsConnected => _store.Load().SmartLights.Devices.Count > 0;

    /// <summary>True when the id belongs to a paired smart light (brand prefix
    /// matches a registered driver and the device is configured).</summary>
    public bool Owns(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var brand = BrandOf(id);
        return brand is not null && _drivers.ContainsKey(brand);
    }

    private static string? BrandOf(string id)
    {
        var c = id.IndexOf(':');
        return c > 0 ? id.Substring(0, c) : null;
    }

    private ILightDriver? DriverForId(string id)
    {
        var brand = BrandOf(id);
        return brand is not null && _drivers.TryGetValue(brand, out var d) ? d : null;
    }

    // ── ILightingDeviceProvider ──────────────────────────────────────────────

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse();
        var settings = _store.Load();
        var devices = settings.SmartLights.Devices;
        if (devices.Count == 0) return resp;
        resp.IsInit = true;

        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;

        for (var i = 0; i < devices.Count; i++)
        {
            var cfg = devices[i];
            if (!cfg.Enabled) continue;

            var isOn = !disabled.Contains(cfg.Id);
            var brightness = 100;
            var hue = 0f;
            var saturation = 1f;
            if (prefs.TryGetValue(cfg.Id, out var pref))
            { brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation; }

            var (defX, defY, defW, defH) = DefaultLayout(i);
            layouts.TryGetValue(cfg.Id, out var layout);

            resp.Devices.Add(new LightingDevice
            {
                Id = cfg.Id,
                Name = cfg.Name,
                Type = "ledstrip",
                IconType = IconType,
                LedsOn = isOn,
                Brightness = brightness,
                Hue = hue,
                Saturation = saturation,
                LedCount = 1,
                CanvasX = layout?.X ?? defX,
                CanvasY = layout?.Y ?? defY,
                CanvasW = layout?.W ?? defW,
                CanvasH = layout?.H ?? defH,
                CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
                ZoneType = "linear",
                ZoneResizable = false,
            });
        }
        return resp;
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        // ids = the complete set of MY devices that should be disabled. Replace
        // only my entries in the shared list; leave other providers' ids alone.
        var set = new HashSet<string>(s.Devices.DisabledLightingDevices, StringComparer.Ordinal);
        foreach (var cfg in s.SmartLights.Devices) set.Remove(cfg.Id);
        foreach (var id in ids) if (Owns(id)) set.Add(id);
        s.Devices.DisabledLightingDevices = new List<string>(set);
    });

    public void SetPower(string id, bool on)
    {
        _store.Update(s =>
        {
            var current = s.Devices.DisabledLightingDevices;
            if (on)
            {
                if (!current.Contains(id)) return;
                var next = new List<string>(current.Count);
                foreach (var x in current) if (x != id) next.Add(x);
                s.Devices.DisabledLightingDevices = next;
            }
            else
            {
                if (current.Contains(id)) return;
                var next = new List<string>(current.Count + 1) ;
                next.AddRange(current); next.Add(id);
                s.Devices.DisabledLightingDevices = next;
            }
        });
        PushStatic(id);
    }

    public void SetBrightness(string id, int brightness)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Brightness = Math.Clamp(brightness, 0, 100);
        });
        PushStatic(id);
    }

    public void SetHue(string id, float hue)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Hue = hue;
        });
        PushStatic(id);
    }

    public void SetSaturation(string id, float saturation)
    {
        _store.Update(s =>
        {
            if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
            { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
            pref.Saturation = saturation;
        });
        PushStatic(id);
    }

    public void SetZoneLedCount(string id, int count) { _ = id; _ = count; }

    public void Identify(string id, int durationMs)
    {
        var dev = Resolve(id);
        var driver = DriverForId(id);
        if (dev is null || driver is null) return;
        _ = driver.IdentifyAsync(dev, CancellationToken.None);
    }

    // ── ILightingFrameContributor ────────────────────────────────────────────

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var settings = _store.Load();
        var devices = settings.SmartLights.Devices;
        var layouts = settings.Lighting.DeviceLayouts;

        // Refresh the per-tick lookup cache from the persisted config.
        _cache.Clear();

        if (devices.Count == 0) { _frames.Clear(); return Array.Empty<DeviceFrame>(); }

        var result = new List<DeviceFrame>(devices.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var idx = startingIndex;
        for (var i = 0; i < devices.Count; i++)
        {
            var cfg = devices[i];
            if (!cfg.Enabled) continue;
            _cache[cfg.Id] = ToSmartLight(cfg);
            live.Add(cfg.Id);

            var driver = DriverForId(cfg.Id);
            var plan = driver?.PlanFrames(_cache[cfg.Id]) ?? new LightFramePlan(16, AverageToSingle: true);
            _plans[cfg.Id] = plan;
            var ledCount = plan.LedCount;

            var (defX, defY, defW, defH) = DefaultLayout(i);
            layouts.TryGetValue(cfg.Id, out var layout);
            var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);

            if (!_frames.TryGetValue(cfg.Id, out var frame)
                || frame.Index != idx || frame.LedCount != ledCount)
            {
                frame = new DeviceFrame(idx, cfg.Id, ledCount,
                    layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
                ApplySampleMap(frame, plan);
                _frames[cfg.Id] = frame;
            }
            else
            {
                frame.X = layout?.X ?? defX; frame.Y = layout?.Y ?? defY;
                frame.W = layout?.W ?? defW; frame.H = layout?.H ?? defH; frame.Rotation = rot;
                // Re-paired devices can change their zone geometry without
                // changing the count — re-apply the sample map both ways
                // (fresh UVs, or back to the grid when UVs disappeared).
                ApplySampleMap(frame, plan);
            }
            result.Add(frame);
            idx++;
        }

        // Drop frames for unpaired devices.
        if (_frames.Count != live.Count)
        {
            var stale = new List<string>();
            foreach (var k in _frames.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frames.Remove(k);
        }
        return result;
    }

    /// <summary>Map engine sample points for the device: real per-zone canvas
    /// positions when the driver provides them (Nanoleaf panel layout),
    /// otherwise an even grid across the rect (region averaging).</summary>
    private static void ApplySampleMap(DeviceFrame frame, LightFramePlan plan)
    {
        if (plan.LedU is { } u && plan.LedV is { } v
            && u.Length == frame.LedCount && v.Length == frame.LedCount)
        {
            frame.LedU = u;
            frame.LedV = v;
            return;
        }
        BuildSampleGrid(frame);
    }

    /// <summary>Spread N sample points across the device's canvas rect so a
    /// single-color lamp averages a region (good screen-mirror behavior).</summary>
    private static void BuildSampleGrid(DeviceFrame frame)
    {
        var n = frame.LedCount;
        if (n <= 1) return;
        var side = (int)Math.Ceiling(Math.Sqrt(n));
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            var col = i % side;
            var row = i / side;
            u[i] = side > 1 ? (col + 0.5f) / side : 0.5f;
            v[i] = side > 1 ? (row + 0.5f) / side : 0.5f;
        }
        frame.LedU = u;
        frame.LedV = v;
    }

    // ── Streaming + static control (called by the writer + control methods) ───

    /// <summary>Submit one engine effect frame for a device: averaged to a
    /// single color, plus the per-zone RGB triplets when the device's plan
    /// requests them. Called by the frame writer each tick.</summary>
    public void SubmitEffectFrame(string id, ReadOnlySpan<byte> ledBytes, int ledCount, float brightness01)
    {
        var (r, g, b) = AverageRgb(ledBytes, ledCount);
        byte[]? zones = null;
        if (_plans.TryGetValue(id, out var plan) && !plan.AverageToSingle)
        {
            var len = Math.Min(ledCount * 3, ledBytes.Length);
            if (len >= 3)
            {
                zones = new byte[len];
                ledBytes.Slice(0, len).CopyTo(zones);
            }
        }
        AccumulateOrSubmit(id, new LightFrame(On: true, r, g, b, brightness01, zones));
    }

    internal static (byte r, byte g, byte b) AverageRgb(ReadOnlySpan<byte> leds, int ledCount)
    {
        if (ledCount <= 0 || leds.Length < 3) return (0, 0, 0);
        long sr = 0, sg = 0, sb = 0;
        var n = Math.Min(ledCount, leds.Length / 3);
        for (var i = 0; i < n; i++)
        {
            var off = i * 3;
            sr += leds[off]; sg += leds[off + 1]; sb += leds[off + 2];
        }
        if (n == 0) return (0, 0, 0);
        return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }

    /// <summary>Submit the latest desired frame for a device (effect streaming).
    /// No-op if the device or its driver isn't resolvable.</summary>
    public void SubmitFrame(string id, LightFrame frame)
    {
        if (!_cache.TryGetValue(id, out var dev)) return;
        var driver = DriverForId(id);
        if (driver is null) return;
        var minInterval = driver.MinIntervalMs(dev);
        _throttle.Submit(id, frame, minInterval, async (f, c) =>
        {
            try { await driver.SendAsync(dev, f, c).ConfigureAwait(false); _online[id] = true; }
            catch { _online[id] = false; throw; }
        }, hostKey: driver.RateLimitKey(dev), hostIntervalMs: minInterval);
    }

    /// <summary>Route an effect frame: session-streaming drivers (Hue
    /// Entertainment) accumulate into a per-controller batch; others go through
    /// the per-light throttle/REST path. Brightness is pre-applied for the
    /// session path (its packet carries raw RGB); off = black.</summary>
    public void AccumulateOrSubmit(string id, LightFrame frame)
    {
        var driver = DriverForId(id);
        if (driver is ISessionStreamer ss && _cache.TryGetValue(id, out var dev))
        {
            byte r = 0, g = 0, b = 0;
            if (frame.On)
            {
                var k = Math.Clamp(frame.Brightness01, 0f, 1f);
                r = (byte)(frame.R * k); g = (byte)(frame.G * k); b = (byte)(frame.B * k);
            }
            ss.Accumulate(dev, r, g, b);
        }
        else
        {
            SubmitFrame(id, frame);
        }
    }

    /// <summary>Flush this tick's batched frames to any active session streamers.</summary>
    public void FlushStreaming()
    {
        foreach (var d in _drivers.Values) if (d is ISessionStreamer ss) ss.Flush();
    }

    /// <summary>End all streaming sessions (effect stopped).</summary>
    public void StopStreamingSessions()
    {
        foreach (var d in _drivers.Values) if (d is ISessionStreamer ss) ss.StopAll();
    }

    /// <summary>Push each device's static (manual) color — used when an effect
    /// stops so lamps return to their configured color rather than freezing on
    /// the last effect frame.</summary>
    public void RestoreStatic()
    {
        var settings = _store.Load();
        foreach (var cfg in settings.SmartLights.Devices)
        {
            if (!cfg.Enabled) continue;
            PushStaticFrom(cfg.Id, settings);
        }
    }

    private void PushStatic(string id)
    {
        // Resolve a fresh snapshot (control can run before BuildFrames populated
        // the cache, e.g. right after pairing).
        if (!_cache.ContainsKey(id))
        {
            var dev = Resolve(id);
            if (dev is not null) _cache[id] = dev;
        }
        PushStaticFrom(id, _store.Load());
    }

    private void PushStaticFrom(string id, NexusSettings s)
    {
        var disabled = s.Devices.DisabledLightingDevices.Contains(id);
        float hue = 0f, sat = 1f; int bri = 100;
        if (s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { hue = pref.Hue; sat = pref.Saturation; bri = pref.Brightness; }
        var global = Math.Clamp(s.Lighting.GlobalBrightness, 0f, 1f);
        var (r, g, b) = ColorMath.HsvToRgb(hue, sat, 1f);
        var b01 = global * Math.Clamp(bri, 0, 100) / 100f;
        SubmitFrame(id, new LightFrame(On: !disabled, r, g, b, b01));
    }

    // ── Routes surface (discover / pair / list / remove) ─────────────────────

    public async Task<GetSmartLightsResponse> GetSmartLightDtosAsync(CancellationToken ct)
    {
        var resp = new GetSmartLightsResponse();
        var devices = _store.Load().SmartLights.Devices;
        if (devices.Count == 0) return resp;

        // Reachability is probed ONCE per (brand, host), not inferred from past
        // send failures — so a transient streaming 429 doesn't strand a whole
        // bridge's lights as "offline". Every light on a reachable bridge is
        // reported online.
        var hostOnline = new Dictionary<(string brand, string host), bool>();
        foreach (var cfg in devices)
        {
            var key = (cfg.Brand, cfg.Host);
            if (hostOnline.ContainsKey(key)) continue;
            var driver = DriverForId(cfg.Id);
            var online = false;
            if (driver is not null)
            {
                try { online = await driver.PingAsync(ToSmartLight(cfg), ct).ConfigureAwait(false); }
                catch { online = false; }
            }
            hostOnline[key] = online;
        }

        foreach (var cfg in devices)
        {
            var online = hostOnline.TryGetValue((cfg.Brand, cfg.Host), out var on) && on;
            _online[cfg.Id] = online; // keep the internal hint in sync with the probe
            resp.Devices.Add(new SmartLightDto
            {
                Id = cfg.Id,
                Brand = cfg.Brand,
                Name = cfg.Name,
                Host = cfg.Host,
                Online = online,
                Enabled = cfg.Enabled,
                LedCount = 1,
            });
        }
        return resp;
    }

    public async Task<DiscoverSmartLightsResponse> DiscoverAsync(string brand, CancellationToken ct)
    {
        var resp = new DiscoverSmartLightsResponse();
        if (!_drivers.TryGetValue(brand, out var driver))
        { resp.Error = "unknown-brand"; return resp; }

        IReadOnlyList<DiscoveredLight> found;
        try { found = await driver.DiscoverAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { resp.Error = ex.Message; return resp; }

        var pairedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in _store.Load().SmartLights.Devices)
            if (!string.IsNullOrEmpty(cfg.StableKey)) pairedKeys.Add(cfg.StableKey);

        foreach (var d in found)
        {
            resp.Devices.Add(new DiscoveredSmartLightDto
            {
                Brand = d.Brand,
                Host = d.Host,
                Name = d.Name,
                StableKey = d.StableKey,
                AlreadyPaired = pairedKeys.Contains(d.StableKey),
            });
        }
        resp.Ok = true;
        return resp;
    }

    public async Task<PairSmartLightResponse> PairAsync(PairSmartLightBody body, CancellationToken ct)
    {
        if (!_drivers.TryGetValue(body.Brand, out var driver))
            return new PairSmartLightResponse { Ok = false, Error = "unknown-brand", Message = "Unknown brand." };

        var target = new DiscoveredLight(body.Brand, body.Host,
            string.IsNullOrEmpty(body.Name) ? body.Brand : body.Name, body.StableKey ?? "");

        PairResult result;
        try { result = await driver.PairAsync(target, ct).ConfigureAwait(false); }
        catch (Exception ex) { return new PairSmartLightResponse { Ok = false, Error = "exception", Message = ex.Message }; }

        if (!result.Ok)
        {
            var msg = result.Error == "link-button"
                ? "Press the button on the bridge, then try again."
                : result.Error;
            return new PairSmartLightResponse { Ok = false, Error = result.Error, Message = msg };
        }

        var added = 0;
        _store.Update(s =>
        {
            // Rebuild the list (replace reference, never structurally mutate the
            // live list a reader on the writer thread may be enumerating). Re-pair
            // replaces existing entries in place of order; new lights append.
            var repl = new Dictionary<string, SmartLightConfig>(StringComparer.Ordinal);
            foreach (var dev in result.Devices) repl[dev.Id] = dev;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var next = new List<SmartLightConfig>(s.SmartLights.Devices.Count + result.Devices.Count);
            foreach (var c in s.SmartLights.Devices)
            {
                if (repl.TryGetValue(c.Id, out var updated)) { next.Add(updated); seen.Add(c.Id); }
                else { next.Add(c); }
            }
            foreach (var dev in result.Devices)
                if (seen.Add(dev.Id)) { next.Add(dev); added++; }
            s.SmartLights.Devices = next;
        });

        // Drop throttle loops for these ids so the next send rebuilds with the
        // refreshed host/token/clientkey (a re-pair can change them).
        foreach (var dev in result.Devices) _throttle.Remove(dev.Id);

        ServiceLog.Info($"[smart-lights] paired {body.Brand}: {result.Devices.Count} light(s), {added} new");
        FireChanged();
        return new PairSmartLightResponse { Ok = true, Added = added, Message = $"Added {result.Devices.Count} light(s)." };
    }

    public void Remove(string id)
    {
        _store.Update(s =>
        {
            // Replace the list reference (not RemoveAt) so a reader enumerating
            // the old reference on the writer thread isn't structurally mutated.
            var next = new List<SmartLightConfig>(s.SmartLights.Devices.Count);
            foreach (var c in s.SmartLights.Devices) if (c.Id != id) next.Add(c);
            s.SmartLights.Devices = next;
        });
        _throttle.Remove(id);
        _online.TryRemove(id, out _);
        _cache.TryRemove(id, out _);
        _plans.TryRemove(id, out _);
        FireChanged();
    }

    /// <summary>Enable/disable a paired light WITHOUT unpairing it. Disabled
    /// lights stay listed on the Smart Lights page but drop off the lighting
    /// canvas/effects (GetAll + BuildFrames skip <c>!Enabled</c>).</summary>
    public void SetEnabled(string id, bool enabled)
    {
        _store.Update(s =>
        {
            foreach (var cfg in s.SmartLights.Devices)
                if (cfg.Id == id) { cfg.Enabled = enabled; break; }
        });
        if (!enabled) _throttle.Remove(id); // stop any in-flight sends
        FireChanged();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private SmartLight? Resolve(string id)
    {
        foreach (var cfg in _store.Load().SmartLights.Devices)
            if (cfg.Id == id) return ToSmartLight(cfg);
        return null;
    }

    private SmartLight ToSmartLight(SmartLightConfig cfg) => new()
    {
        Id = cfg.Id,
        Brand = cfg.Brand,
        Name = cfg.Name,
        Host = cfg.Host,
        StableKey = cfg.StableKey,
        Token = cfg.Token,
        Extra = cfg.Extra,
        Enabled = cfg.Enabled,
        Online = !_online.TryGetValue(cfg.Id, out var on) || on,
    };

    private void FireChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private static (float x, float y, float w, float h) DefaultLayout(int index)
        => (40f + (index % 6) * 130f, 760f + (index / 6) * 80f, 120f, 60f);
}
