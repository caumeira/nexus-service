using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors;

/// <summary>
/// Shared singleton wrapping the LibreHardwareMonitor Computer instance.
/// Both the sensor provider and the fan control provider share this to avoid
/// resource duplication and driver conflicts.
///
/// Thread safety: the lock in Update() ensures only one caller updates at a
/// time. Reads of sensor.Value after an Update() are safe without locking
/// (they return the last-cached value).
///
/// Boot semantics: LHM's <see cref="Computer.Open"/> loads its kernel driver,
/// walks ACPI / SMBIOS / PCI / SuperIO and costs ~50 MB working set plus 1–3 s
/// (2.5 s on T1 with IT8696E + nvidia GPU). To keep host startup off that
/// critical path, the ctor schedules Open() on the thread pool and returns
/// immediately. <see cref="Update"/> no-ops until Open() finishes, so any
/// /sensors or /cooling/* request issued in the warmup window returns empty
/// data instead of blocking - the dashboard hydrates on its next poll. On a
/// healthy boot the PawnIoBootGate wait below resolves in well under a
/// second; on an install/repair boot it holds Open until the driver is
/// usable. AutoRestoreOnStart waits for the open to finish before applying a
/// preset (via SignalLhmOpened), and CurveEngine re-drives channels as they
/// appear, so the delayed open never strands fan control.
/// </summary>
public sealed class LhmComputer : IDisposable
{
    private readonly Computer _computer;
    private readonly Task _openTask;
    private readonly object _updateLock = new();
    private long _lastUpdateTicks;
    private long _lastStorageTicks;

    // Hard cap of one hardware walk per second. Measured from the walk's START,
    // not its end, so the cap is a real 1Hz rather than 1Hz-plus-walk-duration
    // (a walk costs ~100ms). 990 rather than 1000 so the 1s tickers
    // (CurveEngine, MetricsSampler, MonitoringBroadcaster) always clear it
    // despite timer jitter instead of beating against it and skipping every
    // other tick, which would age their data to 2s.
    private const long NormalFloorMs = 990;

    // Fan calibration samples RPM every 250ms to detect settle; the normal
    // floor would feed it the same cached value four times.
    private const long FastFloorMs = 100;

    // SMART/NVMe attributes are read through an ATA pass-through to the drive
    // itself, and on a rotational drive each read parks and unparks the heads.
    // No reader actually needs this cadence: history and diagnostics take drive
    // temperature from SmartHealthMonitor's own 10 minute snapshot, and every
    // live reader marks demand below. It exists as a backstop, bounding how
    // stale a reader that forgets to mark can get. Throughput/activity/free
    // space sensors live in the same node and age with it.
    private const long StorageIntervalMs = 60_000;

    // ...but a client actually looking at drive data gets it every walk, which
    // the 1Hz cap makes per-second. WantStorage is called by the reads that
    // surface it (storage components with SMART, sensor extras), so "someone is
    // watching" needs no plumbing: it is simply whether one of those was asked
    // for recently. Two seconds outlives the 1s poll the details page makes.
    private const long StorageDemandWindowMs = 2_000;
    private long _storageWantedTicks = long.MinValue / 2;

    public LhmComputer(IConfigStore config)
    {
        // With GPU disabled LHM never constructs its AMD/NVIDIA GPU nodes, so
        // no ADL FrameMetrics/PMLog session is opened and no per-node D3DKMT
        // statistics are queried for the process lifetime (the
        // DisableGpuMonitoring diagnostic switch - see NexusSettings).
        var gpuEnabled = !config.Load().DisableGpuMonitoring;
        if (!gpuEnabled)
        {
            Console.WriteLine("[lhm] GPU monitoring disabled by settings");
        }
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = gpuEnabled,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _openTask = Task.Run(async () =>
        {
            // Open() enumerates SuperIO and DIMM SPD exactly once, so a probe
            // lost to another SMBus master is a sensor missing for the whole
            // session. The user-configured window yields the boot-time bus
            // burst first; zero by default.
            await StartupDelayGate.WaitAsync().ConfigureAwait(false);

            // Open() enumerates SuperIO exactly once, and it needs the PawnIO
            // device up - wait for the boot-time install/repair to finish so
            // a just-repaired driver yields motherboard sensors in the same
            // boot. Capped in the gate; no-op on unarmed hosts.
            await PawnIoBootGate.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            try
            {
                _computer.Open();
                Console.WriteLine($"[lhm] background open complete in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lhm] background open failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
            finally
            {
                PawnIoBootGate.SignalLhmOpened();
            }
        });
    }

    public Computer Instance => _computer;

    /// <summary>
    /// Completes when the background <see cref="Computer.Open"/> has finished
    /// (or failed). Consumers that need fully-enumerated hardware before they
    /// read sensor / hardware-name data should await this. Completes even if
    /// Open() threw - callers should still handle empty Hardware collections.
    /// </summary>
    public Task OpenTask => _openTask;

    /// <summary>
    /// Update all hardware sensors. Thread-safe - concurrent callers are serialized.
    /// Capped at one hardware walk per second, which collapses the redundant
    /// updates from multiple callers (MonitoringBroadcaster, CurveEngine, HTTP
    /// handlers) into a single iteration; callers inside that window read the
    /// values the previous walk cached. <see cref="SensorRefresh.Fast"/> lowers
    /// the cap for fan calibration, <see cref="SensorRefresh.Force"/> bypasses
    /// it and refreshes every group.
    /// Returns immediately if the background <see cref="Computer.Open"/> hasn't
    /// finished yet - callers see an empty <see cref="Computer.Hardware"/>
    /// collection and degrade to "no sensors" until warmup completes.
    /// </summary>
    /// <summary>
    /// Signals that a caller is about to read drive data, so the next walks
    /// refresh the storage group instead of leaving it on its idle cadence.
    /// Call it immediately before <see cref="Update"/>.
    /// </summary>
    public void WantStorage() => Volatile.Write(ref _storageWantedTicks, Environment.TickCount64);

    public void Update(SensorRefresh refresh = SensorRefresh.Normal)
    {
        if (!_openTask.IsCompletedSuccessfully) return;

        lock (_updateLock)
        {
            var now = Environment.TickCount64;
            if (refresh != SensorRefresh.Force)
            {
                var floorMs = refresh == SensorRefresh.Fast ? FastFloorMs : NormalFloorMs;
                if (now - _lastUpdateTicks < floorMs)
                {
                    return;
                }
            }

            var storageDue = refresh == SensorRefresh.Force
                || now - Volatile.Read(ref _storageWantedTicks) <= StorageDemandWindowMs
                || now - _lastStorageTicks >= StorageIntervalMs;

            _lastUpdateTicks = now;
            // LibreHardwareMonitor's IntelCpu.Update sleeps 1ms per physical core
            // in its core-clock loop. At Windows' default 15.6ms timer tick that
            // is ~188ms on a 12-core part - 57% of the whole walk, all of it
            // sleeping. Holding 1ms resolution across the walk turns each of
            // those into ~1-2ms.
            using (WindowsTimerResolution.Elevate())
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Storage && !storageDue) continue;

                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                        sub.Update();
                }
            }

            if (storageDue) _lastStorageTicks = now;
        }
    }

    public void Dispose()
    {
        // Wait for the background open to finish before Close(): LHM's
        // Computer.Close() isn't documented thread-safe against an in-flight
        // Open(), and Dispose() is only called at service shutdown so the
        // wait is rare and not on any user-visible path.
        try { _openTask.Wait(); } catch { /* shutdown best-effort */ }
        _computer.Close();
    }
}


/// <summary>Holds the process timer resolution at 1ms for the duration of the
/// scope. LibreHardwareMonitor's CPU update sleeps 1ms per core; at the default
/// 15.6ms tick those sleeps dominate the whole sensor walk.</summary>
internal static partial class WindowsTimerResolution
{
    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    private const uint PeriodMs = 1;
    private const uint TimerrNoError = 0;

    internal static Scope Elevate() => new(TimeBeginPeriod(PeriodMs) == TimerrNoError);

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _held;
        internal Scope(bool held) => _held = held;
        public void Dispose()
        {
            if (_held) TimeEndPeriod(PeriodMs);
        }
    }
}
