using System.Diagnostics;
using System.Runtime.InteropServices;
using Qos.Service.Models.Sensors;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Qos.Service.Fps;

public sealed class WindowsFpsProvider : IFpsProvider
{
    private const int FocusScanDelayMs = 250;
    private const int StaleFrameMs = 2000;
    private const float GaugeMaximumFps = 240f;
    private const string SensorName = "FPS";
    private static readonly string SessionName = $"qOS-Fps-{Environment.ProcessId}";
    private static readonly Guid DxgKrnlProviderGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly TraceEventID PresentInfoEventId = (TraceEventID)0x00b8;

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
            var value = isFresh ? (float)Math.Max(0, _fps) : 0f;
            var formatted = isFresh ? $"{value:F0} fps" : "-";

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
                        Value = value,
                        Min = 0,
                        Max = value,
                        Average = value,
                        Usage = value,
                        TheoreticalMaximum = GaugeMaximumFps,
                        Units = "fps",
                        Formatted = formatted,
                        FormattedMin = "0 fps",
                        FormattedMax = $"{GaugeMaximumFps:F0} fps",
                        FormattedAverage = formatted,
                        FormattedUsage = formatted,
                        Parent = new SensorParent { Id = "fps", Name = parentName },
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
                var target = GetForegroundTarget();
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

    private static (int Pid, string Name) GetForegroundTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return (0, "");

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return (0, "");

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return ((int)pid, process.ProcessName);
        }
        catch
        {
            return ((int)pid, $"PID {pid}");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
