using System.Runtime.InteropServices;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Sensors;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Nexus.Service.Fps;

public sealed class WindowsFpsProvider : IFpsProvider
{
    private const int FocusScanDelayMs = 250;
    private const int StaleFrameMs = 2000;
    private const float GaugeMaximumFps = 240f;
    private const float GaugeMaximumFrameMs = 50f;
    private const string SensorName = "FPS";
    private const string FrameTimeName = "Frame Time";
    private static readonly string SessionName = $"Nexus-Fps-{Environment.ProcessId}";
    private static readonly Guid DxgKrnlProviderGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly TraceEventID PresentInfoEventId = (TraceEventID)0x00b8;

    // The nexus-service runs as LocalSystem in Session 0, which has no
    // interactive desktop — GetForegroundWindow() from here always returns
    // nothing useful. The user-session helper polls foreground via
    // ScreenTimePoller and publishes the PID through IScreenTimeProvider,
    // so we read the target PID from there.
    private readonly IScreenTimeProvider _screenTime;
    private readonly object _gate = new();
    private readonly FpsCalculator _calculator = new();

    private CancellationTokenSource? _cts;
    private Task? _focusTask;
    private Task? _traceTask;
    private TraceEventSession? _session;
    private int _targetPid;
    private string _targetName = "";
    private double _fps;
    private bool _hasValue;
    private DateTime _lastPresentUtc = DateTime.MinValue;
    private DateTime _nextStartAllowedUtc = DateTime.MinValue;
    private DateTime _lastErrorLoggedUtc = DateTime.MinValue;

    public WindowsFpsProvider(IScreenTimeProvider screenTime)
    {
        _screenTime = screenTime;
    }

    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null || DateTime.UtcNow < _nextStartAllowedUtc)
                return;

            cts = new CancellationTokenSource();
            _cts = cts;
            ResetTargetLocked();
        }

        var focusTask = Task.Run(() => FocusLoopAsync(cts.Token), cts.Token);
        var traceTask = Task.Run(() => TraceLoop(cts.Token), cts.Token);
        lock (_gate)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _focusTask = focusTask;
                _traceTask = traceTask;
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? focusTask;
        Task? traceTask;
        TraceEventSession? session;
        lock (_gate)
        {
            cts = _cts;
            if (cts is null)
                return;

            _cts = null;
            focusTask = _focusTask;
            traceTask = _traceTask;
            _focusTask = null;
            _traceTask = null;
            session = _session;
            _session = null;
            ResetTargetLocked();
        }

        try { cts.Cancel(); } catch { }
        StopSession(session);

        _ = Task.WhenAll(IgnoreFaults(focusTask), IgnoreFaults(traceTask))
            .ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    public HardwareComponent GetComponent()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return new HardwareComponent
                {
                    Id = "fps",
                    Name = SensorName,
                    Sensors = new List<HardwareSensor>(),
                };
            }

            var parentName = string.IsNullOrWhiteSpace(_targetName)
                ? SensorName
                : $"{SensorName} ({_targetName})";
            var isFresh = _hasValue
                && (DateTime.UtcNow - _lastPresentUtc).TotalMilliseconds <= StaleFrameMs;
            var fpsValue = isFresh ? (float)Math.Max(0, _fps) : 0f;
            var fpsFormatted = isFresh ? $"{fpsValue:F0}" : "-";
            var frameMs = isFresh && fpsValue > 0 ? 1000f / fpsValue : 0f;
            var frameMsFormatted = isFresh && fpsValue > 0 ? $"{frameMs:F1} ms" : "-";
            var parent = new SensorParent { Id = "fps", Name = parentName };

            return new HardwareComponent
            {
                Id = "fps",
                Name = parentName,
                Sensors = new List<HardwareSensor>
                {
                    new()
                    {
                        Id = "fps/current",
                        Name = SensorName,
                        Type = "Framerate",
                        Value = fpsValue,
                        Min = 0,
                        Max = fpsValue,
                        Average = fpsValue,
                        Usage = fpsValue,
                        TheoreticalMaximum = GaugeMaximumFps,
                        Units = "fps",
                        Formatted = fpsFormatted,
                        FormattedMin = "0",
                        FormattedMax = $"{GaugeMaximumFps:F0}",
                        FormattedAverage = fpsFormatted,
                        FormattedUsage = fpsFormatted,
                        Parent = parent,
                    },
                    new()
                    {
                        Id = "fps/frame-time",
                        Name = FrameTimeName,
                        Type = "FrameTime",
                        Value = frameMs,
                        Min = 0,
                        Max = frameMs,
                        Average = frameMs,
                        Usage = frameMs,
                        TheoreticalMaximum = GaugeMaximumFrameMs,
                        Units = "ms",
                        Formatted = frameMsFormatted,
                        FormattedMin = "0 ms",
                        FormattedMax = $"{GaugeMaximumFrameMs:F0} ms",
                        FormattedAverage = frameMsFormatted,
                        FormattedUsage = frameMsFormatted,
                        Parent = parent,
                    },
                },
            };
        }
    }

    public void Dispose() => Stop();

    private async Task FocusLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var target = GetForegroundTargetFromHelper();
                lock (_gate)
                {
                    if (_cts is null || token.IsCancellationRequested)
                        return;

                    if (_targetPid != target.Pid)
                    {
                        _targetPid = target.Pid;
                        _targetName = target.Name;
                        _calculator.Reset();
                        _fps = 0;
                        _hasValue = false;
                        _lastPresentUtc = DateTime.MinValue;
                    }
                }

                await Task.Delay(FocusScanDelayMs, token);
            }
        }
        catch (TaskCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fps] foreground scan failed: {ex.Message}");
        }
    }

    private void TraceLoop(CancellationToken token)
    {
        TraceEventSession? session = null;
        Action<TraceEvent>? onEvent = null;

        try
        {
            TryStopExistingSession();

            session = new TraceEventSession(SessionName);
            session.StopOnDispose = true;
            lock (_gate)
            {
                if (_cts is null || token.IsCancellationRequested)
                    return;
                _session = session;
            }

            using var stopRegistration = token.Register(static state => StopSession((TraceEventSession?)state), session);
            onEvent = HandleTraceEvent;
            session.Source.Dynamic.All += onEvent;
            session.EnableProvider(DxgKrnlProviderGuid);
            session.Source.Process();
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            HandleTraceFailure(ex);
        }
        finally
        {
            if (session is not null)
            {
                if (onEvent is not null)
                    session.Source.Dynamic.All -= onEvent;

                StopSession(session);
                lock (_gate)
                {
                    if (ReferenceEquals(_session, session))
                        _session = null;
                }
            }
        }
    }

    private void HandleTraceEvent(TraceEvent data)
    {
        if (data.ProviderGuid != DxgKrnlProviderGuid || data.ID != PresentInfoEventId)
            return;

        var targetPid = Volatile.Read(ref _targetPid);
        if (targetPid <= 0 || data.ProcessID != targetPid)
            return;

        lock (_gate)
        {
            if (_cts is null || data.ProcessID != _targetPid)
                return;

            _fps = _calculator.AddFrameTicks(data.TimeStamp.Ticks);
            _hasValue = _calculator.Count >= 2;
            _lastPresentUtc = DateTime.UtcNow;
        }
    }

    private void HandleTraceFailure(Exception ex)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _focusTask = null;
            _traceTask = null;
            _session = null;
            _nextStartAllowedUtc = DateTime.UtcNow.AddSeconds(10);
            ResetTargetLocked();
        }

        try { cts?.Cancel(); } catch { }

        var now = DateTime.UtcNow;
        if ((now - _lastErrorLoggedUtc).TotalSeconds >= 30)
        {
            _lastErrorLoggedUtc = now;
            Console.Error.WriteLine($"[fps] ETW capture unavailable: {ex.Message}");
        }
    }

    private void ResetTargetLocked()
    {
        _targetPid = 0;
        _targetName = "";
        _fps = 0;
        _hasValue = false;
        _lastPresentUtc = DateTime.MinValue;
        _calculator.Reset();
    }

    private static async Task IgnoreFaults(Task? task)
    {
        if (task is null)
            return;

        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static void TryStopExistingSession()
    {
        try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); }
        catch { }
        StopSessionByName();
    }

    private static void StopSession(TraceEventSession? session)
    {
        if (session is null)
            return;

        try { session.Source.StopProcessing(); } catch { }
        try { session.Stop(); } catch { }
        try { session.Dispose(); } catch { }
        StopSessionByName();
    }

    private static void StopSessionByName()
    {
        var properties = new EventTraceProperties();
        properties.Wnode.BufferSize = (uint)Marshal.SizeOf<EventTraceProperties>();
        try { _ = ControlTrace(0, SessionName, ref properties, EventTraceControlStop); }
        catch { }
    }

    private (int Pid, string Name) GetForegroundTargetFromHelper()
    {
        FocusSession? session;
        try { session = _screenTime.GetCurrentSession(); }
        catch { return (0, ""); }

        if (session is null) return (0, "");
        if (!int.TryParse(session.Id, out var pid) || pid <= 0) return (0, "");
        return (pid, session.Name ?? "");
    }

    private const uint EventTraceControlStop = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTrace(
        ulong sessionHandle,
        string sessionName,
        ref EventTraceProperties properties,
        uint controlCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public int NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }
}
