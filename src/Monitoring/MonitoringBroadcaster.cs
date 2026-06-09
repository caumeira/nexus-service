using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Subscription-aware broadcaster that checks which topics have subscribers
/// each tick and only gathers data from the required sources. Reads are
/// volatile snapshots (~1 ms each) so Tick gathers them sequentially.
/// </summary>
public sealed class MonitoringBroadcaster : BackgroundService
{
    private readonly ISensorProvider _sensors;
    private readonly ProcessMonitor _processes;
    private readonly INetworkProvider _network;
    private readonly IPerformanceProvider _performance;
    private readonly IScreenTimeProvider _screenTime;
    private readonly IVolumeProvider _volume;
    private readonly IFpsProvider _fps;
    private readonly MultiplexHub _hub;
    private readonly TimeProvider _timeProvider;

    private VolumeState? _lastVolumeBroadcast;

    private int _intervalMs = 1000;
    private long _lastScreenTimeBroadcastTicks;

    // Cached envelope bytes for slow / event-driven topics. Read by the hub on
    // a new subscriber's receive thread; written by Tick on the broadcaster
    // thread. Reference assignment of byte[] is atomic on every supported
    // runtime; volatile gives publication ordering.
    private volatile byte[]? _screenTimeSnapshotBytes;
    private volatile byte[]? _volumeSnapshotBytes;

    private readonly Dictionary<string, (long bytesIn, long bytesOut)> _prevNet = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _prevNetTime = DateTime.MinValue;
    private IReadOnlyList<Models.Activity.NetworkProcessInfo>? _prevNetSnapshot;
    private NetworkFrame? _cachedNetFrame;

    // Reused across ticks to avoid per-cycle allocations on the broadcast path.
    // Tick is single-threaded and the frame is fully serialized into bytes
    // before the next Tick runs, so the list is safe to clear-and-refill.
    private readonly List<string> _gpuModelsBuf = new();

    private const int TopProcesses = 25;
    private const int TopNetwork = 25;
    private static readonly long ScreenTimeBroadcastIntervalTicks = TimeSpan.FromSeconds(10).Ticks;

    public MonitoringBroadcaster(
        ISensorProvider sensors,
        ProcessMonitor processes,
        INetworkProvider network,
        IPerformanceProvider performance,
        IScreenTimeProvider screenTime,
        IVolumeProvider volume,
        IFpsProvider fps,
        MultiplexHub hub)
        : this(sensors, processes, network, performance, screenTime, volume, fps, hub, TimeProvider.System)
    {
    }

    internal MonitoringBroadcaster(
        ISensorProvider sensors,
        ProcessMonitor processes,
        INetworkProvider network,
        IPerformanceProvider performance,
        IScreenTimeProvider screenTime,
        IVolumeProvider volume,
        IFpsProvider fps,
        MultiplexHub hub,
        TimeProvider timeProvider)
    {
        _sensors = sensors;
        _processes = processes;
        _network = network;
        _performance = performance;
        _screenTime = screenTime;
        _volume = volume;
        _fps = fps;
        _hub = hub;
        _timeProvider = timeProvider;
        _hub.OnTopicFirstSubscriber += OnTopicFirstSubscriber;
        _hub.OnTopicLastUnsubscriber += OnTopicLastUnsubscriber;
        _hub.RegisterSnapshotProvider("screentime", GetScreenTimeSnapshot);
        _hub.RegisterSnapshotProvider("volume", GetVolumeSnapshot);
    }

    private ReadOnlyMemory<byte>? GetScreenTimeSnapshot()
    {
        var bytes = _screenTimeSnapshotBytes;
        return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
    }

    private ReadOnlyMemory<byte>? GetVolumeSnapshot()
    {
        var bytes = _volumeSnapshotBytes;
        return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
    }

    public int GetInterval() => _intervalMs;
    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _fps.Stop();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _hub.OnTopicFirstSubscriber -= OnTopicFirstSubscriber;
        _hub.OnTopicLastUnsubscriber -= OnTopicLastUnsubscriber;
        _hub.UnregisterSnapshotProvider("screentime");
        _hub.UnregisterSnapshotProvider("volume");
        _fps.Stop();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[monitoring-broadcaster] cycle failed: {ex.Message}");
            }

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    internal async Task Tick(CancellationToken ct)
    {
        bool composite = _hub.TopicHasSubscribers("monitoring");
        bool needCpu = composite || _hub.TopicHasSubscribers("cpu");
        bool needGpu = composite || _hub.TopicHasSubscribers("gpu");
        bool needMemory = composite || _hub.TopicHasSubscribers("memory");
        bool needStorage = composite || _hub.TopicHasSubscribers("storage");
        bool needMotherboard = composite || _hub.TopicHasSubscribers("motherboard");
        bool needLhm = needCpu || needGpu || needMemory || needStorage || needMotherboard;
        bool needProcesses = composite || _hub.TopicHasSubscribers("processes");
        bool needNetwork = composite || _hub.TopicHasSubscribers("network");
        bool needScreenTime = _hub.TopicHasSubscribers("screentime")
            && ShouldBroadcastScreenTime(_timeProvider.GetUtcNow().UtcTicks);
        bool needVolume = _hub.TopicHasSubscribers("volume");
        bool needFps = _hub.TopicHasSubscribers("fps");
        bool needExtras = _hub.TopicHasSubscribers("extras");

        if (needVolume)
        {
            await BroadcastVolumeIfChangedAsync();
        }

        if (needFps)
        {
            _fps.Start();
        }
        else
        {
            _fps.Stop();
        }

        if (!needLhm && !needProcesses && !needNetwork && !needScreenTime && !needFps && !needExtras)
            return;

        // Process/network/screentime reads are volatile snapshots (<1ms each),
        // gathered sequentially since scheduling them exceeds the read cost.
        ProcessFrame? processFrame = needProcesses ? BuildProcessFrame(ct) : null;
        NetworkFrame? networkFrame = needNetwork ? BuildNetworkFrame() : null;
        ScreenTimeFrame? screenTimeFrame = needScreenTime ? BuildScreenTimeFrame() : null;
        HardwareComponent? fpsComponent = needFps ? _fps.GetComponent() : null;
        HardwareComponent? cpuComponent = needCpu ? BuildCpuComponent() : null;
        List<HardwareComponent>? gpuComponents = needGpu ? BuildGpuComponents() : null;
        HardwareComponent? memoryComponent = needMemory ? BuildMemoryComponent() : null;
        Dictionary<string, StorageComponent>? storageComponents = needStorage
            ? new Dictionary<string, StorageComponent>(_sensors.GetStorageComponents())
            : null;
        HardwareComponent? motherboardComponent = needMotherboard ? BuildMotherboardComponent() : null;
        SensorExtras? extras = needExtras ? _sensors.GetSensorExtras() : null;
        string cpuModel = cpuComponent?.Name ?? "";
        _gpuModelsBuf.Clear();
        if (gpuComponents is not null)
        {
            for (int i = 0; i < gpuComponents.Count; i++)
            {
                _gpuModelsBuf.Add(gpuComponents[i].Name);
            }
        }
        var gpuModels = _gpuModelsBuf;
        string memoryTotal = needMemory ? _sensors.GetMemoryTotalFormatted() : "";
        string motherboardModel = motherboardComponent?.Name ?? "";

        if (composite)
        {
            var frame = new MonitoringFrame
            {
                Cpu = cpuComponent,
                Gpu = gpuComponents,
                Memory = memoryComponent,
                Storage = storageComponents,
                Motherboard = motherboardComponent,
                CpuModel = cpuModel,
                GpuModels = gpuModels,
                MemoryTotal = memoryTotal,
                MotherboardModel = motherboardModel,
                Processes = processFrame,
                Network = networkFrame,
            };

            var envelope = WsEnvelope.Build("monitoring", frame,
                AppJsonContext.Default.MonitoringFrame);
            await _hub.BroadcastTopicAsync("monitoring", envelope);
        }

        // Per-domain sensor topics for clients subscribed beside the composite.
        if (needLhm)
        {
            if (cpuComponent is not null && _hub.TopicHasSubscribers("cpu"))
            {
                var env = WsEnvelope.Build("cpu", cpuComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("cpu", env);
            }
            if (gpuComponents is not null && _hub.TopicHasSubscribers("gpu"))
            {
                var env = WsEnvelope.Build("gpu", gpuComponents, AppJsonContext.Default.ListHardwareComponent);
                await _hub.BroadcastTopicAsync("gpu", env);
            }
            if (memoryComponent is not null && _hub.TopicHasSubscribers("memory"))
            {
                var env = WsEnvelope.Build("memory", memoryComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("memory", env);
            }
            if (storageComponents is not null && _hub.TopicHasSubscribers("storage"))
            {
                var env = WsEnvelope.Build("storage", storageComponents,
                    AppJsonContext.Default.IReadOnlyDictionaryStringStorageComponent);
                await _hub.BroadcastTopicAsync("storage", env);
            }
            if (motherboardComponent is not null && _hub.TopicHasSubscribers("motherboard"))
            {
                var env = WsEnvelope.Build("motherboard", motherboardComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("motherboard", env);
            }
        }

        if (needProcesses && processFrame != null && _hub.TopicHasSubscribers("processes"))
        {
            var env = WsEnvelope.Build("processes", processFrame, AppJsonContext.Default.ProcessFrame);
            await _hub.BroadcastTopicAsync("processes", env);
        }
        if (needNetwork && networkFrame != null && _hub.TopicHasSubscribers("network"))
        {
            var env = WsEnvelope.Build("network", networkFrame, AppJsonContext.Default.NetworkFrame);
            await _hub.BroadcastTopicAsync("network", env);
        }
        if (needScreenTime && screenTimeFrame != null)
        {
            var env = WsEnvelope.Build("screentime", screenTimeFrame, AppJsonContext.Default.ScreenTimeFrame);
            _screenTimeSnapshotBytes = env.ToArray();
            await _hub.BroadcastTopicAsync("screentime", env);
            Interlocked.Exchange(ref _lastScreenTimeBroadcastTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
        if (needFps && fpsComponent != null)
        {
            var env = WsEnvelope.Build("fps", fpsComponent, AppJsonContext.Default.HardwareComponent);
            await _hub.BroadcastTopicAsync("fps", env);
        }
        // Detailed-tab extras: broadcast only when subscribed; the composite
        // frame omits them so other pages don't get data they don't render.
        if (needExtras && extras is not null)
        {
            var env = WsEnvelope.Build("extras", extras, AppJsonContext.Default.SensorExtras);
            await _hub.BroadcastTopicAsync("extras", env);
        }
    }

    private void OnTopicFirstSubscriber(string topic)
    {
        if (string.Equals(topic, "fps", StringComparison.OrdinalIgnoreCase))
        {
            _fps.Start();
        }
        if (string.Equals(topic, "screentime", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _lastScreenTimeBroadcastTicks, 0);
        }
    }

    private void OnTopicLastUnsubscriber(string topic)
    {
        if (string.Equals(topic, "fps", StringComparison.OrdinalIgnoreCase))
            _fps.Stop();
    }

    private HardwareComponent BuildCpuComponent() => new()
    {
        Id = "cpu",
        Name = _sensors.GetCpuModel(),
        Sensors = new List<HardwareSensor>(_sensors.GetCpuSensors()),
    };

    private List<HardwareComponent> BuildGpuComponents()
    {
        // One component per physical GPU, each carrying only its own sensors.
        // Discrete first, so any consumer that still reads gpu[0] defaults to the
        // dGPU rather than whichever the platform enumerated first (often the iGPU);
        // the client resolves its preferred GPU by name on top of this.
        var gpus = _sensors.GetGpus().OrderBy(g => g.Integrated).ToList();
        var result = new List<HardwareComponent>(gpus.Count);
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            result.Add(new HardwareComponent
            {
                Id = $"gpu/{i}",
                Name = g.Name,
                Vendor = g.Vendor,
                Integrated = g.Integrated,
                Sensors = g.Sensors,
            });
        }
        return result;
    }

    private HardwareComponent BuildMemoryComponent() => new()
    {
        Id = "memory",
        Name = "Memory",
        Sensors = new List<HardwareSensor>(_sensors.GetMemorySensors()),
    };

    private HardwareComponent BuildMotherboardComponent() => new()
    {
        Id = "motherboard",
        Name = _sensors.GetMotherboardModel(),
        Sensors = new List<HardwareSensor>(_sensors.GetMotherboardSensors()),
    };

    // Push the system volume on the "volume" topic only when it changed since
    // the last broadcast (tracks within one tick, no wire traffic on idle).
    // Subscribers get a fresh snapshot on connect via the multiplex hub.
    private async Task BroadcastVolumeIfChangedAsync()
    {
        VolumeState state;
        try
        { state = _volume.GetState(); }
        catch { return; }

        if (_lastVolumeBroadcast is { } prev
            && prev.Supported == state.Supported
            && prev.Muted == state.Muted
            && Math.Abs(prev.Volume - state.Volume) < 0.001)
        {
            return;
        }

        _lastVolumeBroadcast = new VolumeState { Supported = state.Supported, Volume = state.Volume, Muted = state.Muted };
        var envelope = WsEnvelope.Build("volume", state, AppJsonContext.Default.VolumeState);
        _volumeSnapshotBytes = envelope.ToArray();
        await _hub.BroadcastTopicAsync("volume", envelope);
    }

    private ProcessFrame BuildProcessFrame(CancellationToken ct)
    {
        var procs = _processes.GetProcesses();
        var snap = _performance.SampleAsync(ct).GetAwaiter().GetResult();

        var count = Math.Min(procs.Count, TopProcesses);
        var top = new List<ProcessEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var p = procs[i];
            top.Add(new ProcessEntry
            {
                Name = p.Name,
                CpuPercent = p.CpuPercent,
                MemoryMb = p.MemoryMb,
            });
        }

        return new ProcessFrame
        {
            Processes = top,
            TotalCpu = snap.Cpu ?? 0,
            TotalMemoryPercent = snap.Memory ?? 0,
        };
    }

    private NetworkFrame BuildNetworkFrame()
    {
        var snapshot = _network.GetSnapshot();

        // nettop runs on its own slower timer. If the snapshot object hasn't
        // changed since the last tick, reuse the cached rates instead of
        // producing zeros from an identical delta.
        if (ReferenceEquals(snapshot, _prevNetSnapshot) && _cachedNetFrame is not null)
            return _cachedNetFrame;
        _prevNetSnapshot = snapshot;

        var now = DateTime.UtcNow;
        var dt = _prevNetTime != DateTime.MinValue
            ? (now - _prevNetTime).TotalSeconds
            : 0;
        _prevNetTime = now;

        var count = Math.Min(snapshot.Count, TopNetwork);
        var entries = new List<NetworkRateEntry>(count);
        if (dt > 0 && dt < 30)
        {
            for (int i = 0; i < count; i++)
            {
                var p = snapshot[i];
                double rateIn = 0, rateOut = 0;
                if (_prevNet.TryGetValue(p.Name, out var prev))
                {
                    var deltaIn = Math.Max(0, p.BytesIn - prev.bytesIn);
                    var deltaOut = Math.Max(0, p.BytesOut - prev.bytesOut);
                    rateIn = deltaIn / dt;
                    rateOut = deltaOut / dt;
                }
                entries.Add(new NetworkRateEntry
                {
                    Name = p.Name,
                    RateIn = rateIn,
                    RateOut = rateOut,
                });
            }
        }

        _prevNet.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var p = snapshot[i];
            _prevNet[p.Name] = (p.BytesIn, p.BytesOut);
        }

        _cachedNetFrame = new NetworkFrame { Entries = entries };
        return _cachedNetFrame;
    }

    private ScreenTimeFrame BuildScreenTimeFrame()
    {
        return new ScreenTimeFrame
        {
            Focus = _screenTime.GetCurrentSession(),
            History = new List<AppUsage>(_screenTime.GetTodayUsage()),
        };
    }

    private bool ShouldBroadcastScreenTime(long nowTicks)
    {
        long lastTicks = Interlocked.Read(ref _lastScreenTimeBroadcastTicks);
        return lastTicks == 0 || nowTicks - lastTicks >= ScreenTimeBroadcastIntervalTicks;
    }
}
