using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Normalized (0..1) crop rectangle the dashboard cropper produced, applied
/// in the ffmpeg transcode so the user's framing fills the panel without letterboxing.</summary>
public readonly record struct TryxVideoCrop(double X, double Y, double W, double H);

/// <summary>
/// Singleton coordinator for a Tryx Panorama AIO device. Owns the open transport,
/// the shared <see cref="TryxPanoramaState"/> snapshot, and the high-level
/// operations the heartbeat worker and REST routes call into.
/// Hot-plug is self-healing: each <see cref="EnsureConnected"/> re-runs port discovery.
/// </summary>
public sealed class TryxPanoramaHub : IDisposable
{
    private readonly ITryxPanoramaPanelDiscovery _discovery;
    private readonly Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> _transportFactory;
    private readonly ISensorProvider _sensors;
    private readonly IConfigStore _configStore;
    private readonly object _lock = new();
    private ITryxPanoramaTransport? _transport;
    private bool _disposed;
    // Set while EnsureLocalCopyAsync holds the adb pull, so the heartbeat does not
    // issue a concurrent adb command against the same device.
    private volatile bool _importInProgress;
    private readonly TryxOverlayConfig _overlay;

    public TryxPanoramaHub(
        ITryxPanoramaPanelDiscovery discovery,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> transportFactory,
        ISensorProvider sensors,
        IConfigStore configStore)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _sensors = sensors;
        _configStore = configStore;

        var saved = configStore.Load().Tryx;
        _overlay = new TryxOverlayConfig
        {
            Stats = saved.OverlayStats,
            Color = saved.OverlayColor,
            Align = saved.OverlayAlign,
            Filter = saved.OverlayFilter,
            Opacity = saved.OverlayOpacity,
        };
        State.CurrentMedia = saved.CurrentMedia;
        State.CurrentMediaIsCustom = saved.CurrentMediaIsCustom;
        State.Brightness = saved.Brightness;
    }

    public TryxPanoramaState State { get; } = new();

    /// <summary>True while an import holds the port; the heartbeat skips STATE-all then.</summary>
    public bool ImportInProgress => _importInProgress;

    public bool IsConnected => _transport is { IsOpen: true };

    public TryxOverlayConfig Overlay => _overlay;

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            foreach (var port in _discovery.Discover())
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    State.AdbSerial = port.AdbSerial;
                    State.PortName = port.PortName;
                    State.ProductId = port.ProductId;
                    State.ModelName = TryxPanoramaProtocol.GetModelName(port.ProductId);
                    State.LastConnectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ServiceLog.Info($"[tryx] connected to {port.PortName} (serial={port.Serial}, adb={port.AdbSerial})");
                    ApplyInitialConfig(t);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tryx] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    // Sends the persisted brightness on fresh connect so the panel picks it up
    // without requiring a dashboard interaction. Called only from inside the
    // EnsureConnected lock after _transport is set; uses the transport reference
    // directly to avoid re-entering EnsureConnected.
    private void ApplyInitialConfig(ITryxPanoramaTransport transport)
    {
        try
        {
            transport.Write(TryxRkProtocol.BuildConfig(State.ScreenEnabled, State.Brightness));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tryx] initial config apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    public bool SendConn()
        => SendOnly(TryxRkProtocol.BuildHeartbeat());

    // The RK sensor/overlay protobuf schema is not decoded yet, so there is no
    // known frame to send here; the heartbeat alone keeps the screen awake.
    public bool SendStateAll() => true;

    public bool SetEnabled(bool enable)
    {
        // Screen on/off rides the same f200.f5 config as brightness (f5.f1 = enable);
        // carry the current brightness so turning the screen back on restores it.
        var ok = SendOnly(TryxRkProtocol.BuildConfig(enable, State.Brightness));
        if (ok) State.ScreenEnabled = enable;
        return ok;
    }

    public bool SetBrightness(int brightness)
    {
        var clamped = Math.Clamp(brightness, 0, 100);
        var ok = SendOnly(TryxRkProtocol.BuildConfig(State.ScreenEnabled, clamped));
        if (ok)
        {
            State.Brightness = clamped;
            _configStore.Update(s => s.Tryx.Brightness = clamped);
        }
        return ok;
    }

    public bool SetPreset(string presetId)
    {
        ServiceLog.Warn("[tryx] SetPreset not yet implemented for RK firmware");
        return false;
    }

    public bool SetFanSmart(int[][]? curve)
    {
        ServiceLog.Warn("[tryx] SetFanSmart not yet implemented for RK firmware");
        return false;
    }

    public bool SetFanFixed(int percent)
    {
        ServiceLog.Warn("[tryx] SetFanFixed not yet implemented for RK firmware");
        return false;
    }

    public bool SetOverlay(TryxOverlayConfig overlay)
    {
        ServiceLog.Warn("[tryx] SetOverlay not yet implemented for RK firmware");
        return false;
    }

    public Task<bool> ImportAndPlayVideoAsync(
        string localPath, string sourceName, TryxVideoCrop? crop, int targetWidth, int targetHeight, CancellationToken ct)
    {
        ServiceLog.Warn("[tryx] ImportAndPlayVideoAsync not yet implemented for RK firmware");
        return Task.FromResult(false);
    }

    /// <summary>Ensures a local copy of <paramref name="deviceFileName"/> exists in the media store,
    /// pulling from the device via adb if necessary. Holds the import-pause for the duration
    /// of the pull so the heartbeat does not issue concurrent adb commands.</summary>
    public Task<bool> EnsureLocalCopyAsync(string deviceFileName, CancellationToken ct)
    {
        if (TryxMediaStore.Exists(deviceFileName))
        {
            return Task.FromResult(true);
        }

        var adbSerial = State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            return Task.FromResult(false);
        }

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return Task.FromResult(false);
        }

        ct.ThrowIfCancellationRequested();

        var destPath = TryxMediaStore.Path(deviceFileName);
        _importInProgress = true;
        try
        {
            Directory.CreateDirectory(TryxMediaStore.StoreDir);
            RunAdb(adbPath, $"-s {adbSerial} pull /sdcard/pcMedia/{deviceFileName} \"{destPath}\"",
                60_000, out _, out _);

            if (!File.Exists(destPath))
            {
                return Task.FromResult(false);
            }

            var ffmpegPath = FfmpegResolver.Path;
            if (ffmpegPath is not null && !File.Exists(TryxThumbnailCache.ThumbPath(deviceFileName)))
            {
                try { TryxThumbnailCache.Write(ffmpegPath, destPath, deviceFileName); } catch { }
                try
                {
                    var dur = TryxThumbnailCache.ProbeDuration(ffmpegPath, destPath);
                    TryxThumbnailCache.WriteDuration(deviceFileName, dur);
                }
                catch { }
            }

            return Task.FromResult(true);
        }
        finally
        {
            _importInProgress = false;
        }
    }

    /// <summary>Selects an already-on-device custom file without re-pushing it.</summary>
    public bool SelectCustomMedia(string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return false;
        if (!SendOnly(TryxPanoramaProtocol.BuildWaterBlockScreen(true))) return false;
        if (!SendOnly(TryxPanoramaProtocol.BuildConfigCustom(State.Brightness, deviceFileName, _overlay))) return false;
        State.CurrentMedia = deviceFileName;
        State.CurrentMediaIsCustom = true;
        State.ScreenEnabled = true;
        _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = true; });
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    /// <summary>
    /// Builds the STATE-all sensor JSON from live ISensorProvider reads.
    /// Sensor name patterns match LHM's naming conventions (Windows path);
    /// Mac/Linux providers return subsets - missing sensors produce 0.
    /// </summary>
    internal string BuildLiveSensorJsonForTest() => BuildLiveSensorJson();

    private string BuildLiveSensorJson()
    {
        var cpuSensors = _sensors.GetCpuSensors();
        var gpuSensors = GetPrimaryGpuSensors();
        var memSensors = _sensors.GetMemorySensors();
        var moboSensors = _sensors.GetMotherboardSensors();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var cpuTemp = RoundSensor(cpuSensors, "Temperature", "Package");
        var cpuLoad = RoundSensor(cpuSensors, "Load", "CPU Total");
        var cpuClock = RoundSensor(cpuSensors, "Clock", "Core Max");
        if (cpuClock == 0) cpuClock = MaxClock(cpuSensors, "Core");
        var cpuPower = RoundSensor(cpuSensors, "Power", "Package");
        var cpuVoltage = RoundSensor(cpuSensors, "Voltage", "VCore");

        // "Core" matches "GPU Core" on NVIDIA/AMD; fall back to first Temperature
        // sensor for GPUs that name the die temperature differently.
        var gpuTemp = (int)Math.Round(
            (FindSensor(gpuSensors, "Temperature", "Core")
             ?? FindSensor(gpuSensors, "Temperature", null))?.Value ?? 0f);
        var gpuLoad = RoundSensor(gpuSensors, "Load", "Core");
        var gpuClock = RoundSensor(gpuSensors, "Clock", "Core");
        var gpuPower = RoundSensor(gpuSensors, "Power", null);
        var gpuVoltage = (int)Math.Round(
            (FindSensor(gpuSensors, "Voltage", "Core")
             ?? FindSensor(gpuSensors, "Voltage", null))?.Value ?? 0f);

        // Memory Used/Available are Data type in GB; convert to integer GB.
        var memUsed = (int)Math.Round(FindSensor(memSensors, "Data", "Used")?.Value ?? 0f);
        var memAvail = (int)Math.Round(FindSensor(memSensors, "Data", "Available")?.Value ?? 0f);
        var memTotal = memUsed + memAvail;
        var memLoad = RoundSensor(memSensors, "Load", null);
        // AMD exposes RAM clock as "Memory" under CPU Clock sensors; Intel/other
        // may expose it under motherboard sensors. Fall back to 0 when unavailable.
        var memClock = (int)Math.Round(
            (FindSensor(cpuSensors, "Clock", "Memory")
             ?? FindSensor(moboSensors, "Clock", "Memory"))?.Value ?? 0f);

        var moboTemp = RoundSensor(moboSensors, "Temperature", null);
        var pchTemp = RoundSensor(moboSensors, "Temperature", "PCH");

        var sb = new StringBuilder(512);
        sb.Append("{\"network\":{\"upload\":0,\"download\":0},");
        sb.Append("\"memory\":{\"total\":").Append(memTotal)
          .Append(",\"used\":").Append(memUsed)
          .Append(",\"load\":").Append(memLoad)
          .Append(",\"temperature\":0,\"speed\":").Append(memClock).Append("},");
        sb.Append("\"cpu\":{\"load\":").Append(cpuLoad)
          .Append(",\"usage\":").Append(cpuLoad)
          .Append(",\"temperature\":").Append(cpuTemp)
          .Append(",\"speedAverage\":").Append(cpuClock)
          .Append(",\"power\":").Append(cpuPower)
          .Append(",\"voltage\":").Append(cpuVoltage).Append("},");
        sb.Append("\"gpu\":{\"load\":").Append(gpuLoad)
          .Append(",\"temperature\":").Append(gpuTemp)
          .Append(",\"fan\":0,\"speed\":").Append(gpuClock)
          .Append(",\"power\":").Append(gpuPower)
          .Append(",\"voltage\":").Append(gpuVoltage).Append("},");
        sb.Append("\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0},");
        sb.Append("\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}],");
        sb.Append("\"motherboard\":{\"temperature\":").Append(moboTemp)
          .Append(",\"pchTemperature\":").Append(pchTemp).Append("},");
        sb.Append("\"timestamp\":").Append(ts).Append('}');
        return sb.ToString();
    }

    private IReadOnlyList<HardwareSensor> GetPrimaryGpuSensors()
    {
        // Match MonitoringBroadcaster: discrete-first ordering; use the first discrete GPU.
        var gpus = _sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static int RoundSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
        => (int)Math.Round(FindSensor(sensors, type, nameContains)?.Value ?? 0f);

    // Cores park independently; max is the headline clock across all matching sensors.
    private static int MaxClock(IReadOnlyList<HardwareSensor> sensors, string nameContains)
    {
        var max = 0f;
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (string.Equals(s.Type, "Clock", StringComparison.OrdinalIgnoreCase)
                && s.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                && s.Value > max)
            {
                max = s.Value;
            }
        }
        return (int)Math.Round(max);
    }

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null ||
                s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    private bool SendOnly(byte[] request)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // The transport already retried a transient write on the held handle,
            // so reaching here is a persistent failure (e.g. the panel was
            // unplugged). Drop the transport so the next EnsureConnected rediscovers
            // and reopens - the retry-on-the-handle above is what prevents the old
            // reopen-on-every-transient-failure churn.
            ServiceLog.Error($"[tryx] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private static void RunAdb(string adbPath, string arguments, int timeoutMs, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            // Drain both pipes concurrently before waiting; a child that fills the
            // OS pipe buffer would otherwise deadlock against a read-after-wait.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                stderr = "timed out";
                return;
            }
            stdout = outTask.GetAwaiter().GetResult();
            stderr = errTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stderr = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

}
