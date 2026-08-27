using System;
using System.Collections.Generic;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Singleton coordinator for the SLV3 wireless link: owns the TX + RX dongle
/// transports, the master identity, the bind/unbind state machine, the
/// device-list poll, RGB pushes, and the PWM keepalive (see
/// plans/lianli-wireless-support.md).
/// All hardware I/O and shared state are guarded by one lock: the connection
/// worker's periodic tick and route-driven bind/unbind/identify calls both touch it.
/// </summary>
public sealed class Slv3Hub : IDisposable
{
    private static readonly byte[] BroadcastMac = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    // Shared read-only default (all null = motherboard-sync) for a chain that
    // has never had a duty set. Never mutated - safe to share across keys.
    private static readonly int?[] DefaultDutyTargets = new int?[Slv3Protocol.PortsPerRecord];

    private readonly ISlv3Discovery _discovery;
    private readonly Func<Slv3PortInfo, ISlv3Transport> _transportFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slv3PendingOp> _pending = new(StringComparer.Ordinal);
    private List<Slv3DeviceRecord> _lastFanRecords = new();

    // Last-known chains keyed by MAC hex. Under RGB traffic the RX misses
    // beacons, so a poll's record set is a SAMPLE of the live chains, not the
    // truth; records merge in and only expire after ChainExpiryMs unseen.
    // Wholesale replacement per poll is what made chains flap in and out of
    // cooling/lighting (Y70 log: "device list: 2 -> 1 -> 2" continuously).
    private readonly Dictionary<string, Slv3KnownChain> _knownChains = new(StringComparer.Ordinal);

    // Per-chain PWM port targets keyed by fan MAC hex; a missing key or a
    // null element means that port follows the motherboard PWM header. Read
    // by SendBindFrameLocked, written by SetPortDuty; both hold _lock.
    private readonly Dictionary<string, int?[]> _dutyTargets = new(StringComparer.Ordinal);

    // Last bind/PWM tuple actually put on the air per chain MAC hex, so the
    // tick can skip a keepalive that would say nothing new. L-Connect gates
    // this frame on NeedSyncPwm; sending it unconditionally re-binds an
    // already-bound chain once a second, and the chain controller reboots on
    // that, taking the wired LCD screens hanging off it down with it (Y70,
    // 2026-08-27: ~20 USB removal/arrival pairs a minute while bound, zero
    // while unbound, hardware-bisected).
    private readonly Dictionary<string, byte[]> _lastPwmSent = new(StringComparer.Ordinal);

    private ISlv3Transport? _tx;
    private ISlv3Transport? _rx;
    private byte[] _masterMac = new byte[Slv3Protocol.MacLength];
    private byte _channel = Slv3Protocol.DefaultChannel;
    private byte _cmdSeq;
    private bool _disposed;
    private bool _videoModeActive;
    private int _videoModePreppedCount;
    // Round-robin cursor into ChannelScanOrder so one connect attempt probes a
    // bounded slice; a full scan completes across successive attempts instead
    // of holding _lock for ~19 s of read timeouts in one call.
    private int _channelScanCursor;

    // A chain unseen this long is treated as gone (powered off / out of range)
    // and dropped; until then it stays listed so downstream devices are stable.
    private const long ChainExpiryMs = 30_000;
    // Unseen this long = telemetry is last-known, surfaced as Stale.
    private const long ChainStaleMs = 3_500;

    // GetDev failure escalation (lian-li-linux controller.rs): 5 consecutive
    // USB-level failures -> UsbResetAnother to the RX MCU (a handle reopen does
    // not reset a wedged radio), at most 3 resets per connection, then give the
    // worker its disconnect/reconnect path.
    private const int RxFailStreakForReset = 5;
    private const int MaxRxResetsPerConnection = 3;
    // Post-reset settle per the reference's 500 ms sleep after USB_ResetAnother.
    private const int RxResetSettleMs = 500;
    private int _rxFailStreak;
    private int _rxResetCount;

    // RF_SaveCfg broadcast repeats after a confirmed bind/unbind (the reference
    // sends 3); spaced one DriveTick apart instead of its 200 ms sleeps.
    private const int SaveCfgRepeats = 3;
    private int _saveCfgSendsRemaining;

    // Ticks a pending bind/unbind may re-send before it is dropped as
    // non-converging (fan unreachable); ~1 s per tick.
    private const int PendingOpTickBudget = 15;

    // Header-repeat tiers for RGB pushes (no CRC on this link - see
    // SendRgbFrame). Reliable = one-shot effect application: 4 repeats spaced
    // ~20 ms or the collided header locks the controller up. Streaming = a
    // continuous frame flow where the next frame supersedes a lost one:
    // 2 repeats, 2 ms apart (lian-li-linux rgb.rs uses this profile for its
    // ~30 fps direct sends; 4x20 ms per frame is what capped ours at 10 fps
    // and starved the telemetry beacon).
    private const int ReliableHeaderRepeats = 4;
    private const int ReliableHeaderGapMs = 20;
    private const int StreamingHeaderRepeats = 2;
    private const int StreamingHeaderGapMs = 2;

    // Device-list records span more than one 10-record page once enough chains
    // are bound (up to MaxSlot=14, plus non-fan devices), so the poll requests
    // ceil(count/10) pages. Clamp so a corrupt count can't trigger a runaway read.
    private const int MaxDeviceListPages = 3;
    private int _lastRecordCount;

    public Slv3Hub(ISlv3Discovery discovery, Func<Slv3PortInfo, ISlv3Transport> transportFactory, Func<long>? nowMs = null)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    // Monotonic ms clock; injectable so tests drive chain expiry deterministically.
    private readonly Func<long> _nowMs;

    public Slv3State State { get; } = new();

    public bool IsConnected => _tx is { IsOpen: true } && _rx is { IsOpen: true };

    /// <summary>Opens the TX + RX dongles and learns our master MAC. Both must open for the link to be usable.</summary>
    public bool EnsureConnected()
    {
        if (_disposed)
        {
            return false;
        }
        if (IsConnected)
        {
            return true;
        }
        lock (_lock)
        {
            if (IsConnected)
            {
                return true;
            }

            Slv3PortInfo? txPort = null;
            Slv3PortInfo? rxPort = null;
            foreach (var port in _discovery.Discover())
            {
                if (port.Role == Slv3DongleRole.Tx)
                {
                    txPort ??= port;
                }
                else if (port.Role == Slv3DongleRole.Rx)
                {
                    rxPort ??= port;
                }
            }
            if (txPort is null || rxPort is null)
            {
                return false;
            }

            try
            {
                _tx = _transportFactory(txPort);
                _rx = _transportFactory(rxPort);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-wireless] open failed: {ex.GetType().Name}: {ex.Message}");
                DisconnectLocked();
                return false;
            }

            if (!MasterInitLocked())
            {
                ServiceLog.Warn("[lianli-wireless] GetMac failed on connect");
                DisconnectLocked();
                return false;
            }

            State.IsConnected = true;
            ServiceLog.Info($"[lianli-wireless] connected (tx={txPort.PortName}, rx={rxPort.PortName}, master={State.MasterMac})");
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectLocked();
        }
    }

    private void DisconnectLocked()
    {
        try { _tx?.Dispose(); } catch { /* best effort */ }
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _tx = null;
        _rx = null;
        State.IsConnected = false;
        State.Fans = Array.Empty<Slv3FanInfo>();
        State.MotherboardPwmPercent = null;
        _lastFanRecords = new List<Slv3DeviceRecord>();
        _knownChains.Clear();
        _lastPwmSent.Clear();
        _rxFailStreak = 0;
        _rxResetCount = 0;
        _saveCfgSendsRemaining = 0;
        _videoModeActive = false;
        _videoModePreppedCount = 0;
    }

    private bool MasterInitLocked()
    {
        if (_tx is null)
        {
            return false;
        }
        // Probe the configured channel first, then a bounded slice of the scan
        // order: a dongle left on another channel by L-Connect only answers
        // GetMac there, and a zero MAC in the reply means "no master on this
        // channel", not success. Each dead probe costs a full 500 ms read
        // timeout under _lock, so one attempt probes at most ScanProbesPerAttempt
        // channels; the cursor resumes there on the worker's next 5 s retry,
        // covering all 39 channels across a few attempts without starving the
        // routes and writer that share the lock.
        if (TryGetMacOnChannelLocked(_channel))
        {
            return true;
        }
        const int ScanProbesPerAttempt = 8;
        var order = Slv3Protocol.ChannelScanOrder();
        for (var probes = 0; probes < ScanProbesPerAttempt && probes < order.Length; _channelScanCursor++)
        {
            var channel = order[_channelScanCursor % order.Length];
            if (channel == _channel)
            {
                continue;
            }
            probes++;
            if (TryGetMacOnChannelLocked(channel))
            {
                ServiceLog.Info($"[lianli-wireless] master found on channel {channel} (scanned from {_channel})");
                _channel = channel;
                State.Channel = channel;
                return true;
            }
        }
        return false;
    }

    private bool TryGetMacOnChannelLocked(byte channel)
    {
        if (_tx is null || !_tx.RfSend(Slv3Protocol.BuildGetMac(channel)))
        {
            return false;
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        if (!Slv3Protocol.TryParseGetMac(reply, out var mac, out _, out var fw)
            || Slv3Protocol.MacIsZero(mac))
        {
            return false;
        }
        _masterMac = mac;
        State.MasterMac = Convert.ToHexString(mac);
        State.TxFirmwareVersion = fw;
        State.Channel = channel;
        return true;
    }

    /// <summary>
    /// One tick of the connection worker: refresh the device list, resolve any
    /// pending bind/unbind against the fresh report, then re-send the bind frame
    /// for every fan bound to us plus any still-pending target. This periodic
    /// re-assert is the firmware's keepalive (plans/lianli-wireless-support.md
    /// section 3) - without it a bound fan reverts to its default. Also
    /// broadcasts the master-clock heartbeat the link expects every tick.
    /// </summary>
    public bool DriveTick()
    {
        lock (_lock)
        {
            if (!IsConnected)
            {
                return false;
            }
            if (!RefreshDeviceListLocked())
            {
                return false;
            }

            ResolvePendingLocked();

            var sends = new Dictionary<string, (Slv3DeviceRecord Record, byte TargetSlot)>(StringComparer.Ordinal);
            foreach (var record in _lastFanRecords)
            {
                if (IsBoundToUsLocked(record) && NeedsBindFrameLocked(record))
                {
                    sends[Convert.ToHexString(record.Mac)] = (record, record.RxType);
                }
            }
            foreach (var op in _pending.Values)
            {
                if (TryFindRecordLocked(op.Mac, out var record))
                {
                    sends[Convert.ToHexString(op.Mac)] = (record, op.TargetSlot);
                }
            }

            foreach (var send in sends.Values)
            {
                SendBindFrameLocked(send.Record, send.TargetSlot);
            }

            SendClockHeartbeatLocked();

            if (_saveCfgSendsRemaining > 0)
            {
                _saveCfgSendsRemaining--;
                SendSaveCfgLocked();
            }
            return true;
        }
    }

    private void ResolvePendingLocked()
    {
        List<string>? resolved = null;
        List<string>? expired = null;
        List<(string Key, Slv3PendingOp Op)>? ticked = null;
        foreach (var (key, op) in _pending)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                var done = op.Unbind
                    ? !IsBoundToUsLocked(record)
                    : IsBoundToUsLocked(record);
                if (done)
                {
                    (resolved ??= new List<string>()).Add(key);
                    continue;
                }
            }
            if (op.TicksRemaining <= 1)
            {
                (expired ??= new List<string>()).Add(key);
            }
            else
            {
                (ticked ??= new List<(string, Slv3PendingOp)>()).Add((key, op with { TicksRemaining = op.TicksRemaining - 1 }));
            }
        }
        if (ticked is not null)
        {
            foreach (var (key, op) in ticked)
            {
                _pending[key] = op;
            }
        }
        if (resolved is not null)
        {
            foreach (var key in resolved)
            {
                _pending.Remove(key);
            }
            // Persist the confirmed binding change to fan flash so it survives
            // a power cycle; a bind without SaveCfg lives only in firmware RAM.
            _saveCfgSendsRemaining = SaveCfgRepeats;
        }
        if (expired is not null)
        {
            foreach (var key in expired)
            {
                ServiceLog.Warn($"[lianli-wireless] bind/unbind for {key} did not converge in {PendingOpTickBudget} ticks, dropping");
                _pending.Remove(key);
            }
        }
    }

    private void SendSaveCfgLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = Slv3Protocol.BuildSaveCfg(_masterMac);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            _tx.RfSend(frame);
        }
    }

    // Pages needed to hold recordCount records at RecordsPerPage each, >=1 and
    // capped at MaxDeviceListPages so a corrupt count can't request a huge read.
    private static byte DeviceListPagesFor(int recordCount) =>
        (byte)Math.Clamp(
            (recordCount + Slv3Protocol.RecordsPerPage - 1) / Slv3Protocol.RecordsPerPage,
            1, MaxDeviceListPages);

    private bool RefreshDeviceListLocked()
    {
        if (_rx is null)
        {
            return false;
        }
        // <=10 records/page - request ceil(count/10) pages so a device list that
        // spans more than one page (up to MaxSlot=14 chains plus non-fan devices)
        // isn't truncated to the first 10. Dropped records vanish from the list,
        // so those chains can't be seen, paired, or confirm a bind. Seed pageCount
        // from the last poll's reported count (the reference auto-tunes the same
        // way); a chain that just appeared is picked up on the next ~1 s poll.
        var pageCount = DeviceListPagesFor(_lastRecordCount);
        if (!_rx.RfSend(Slv3Protocol.BuildGetDev(pageCount)))
        {
            // A failed USB write means the handle itself is dead (replug,
            // suspend); fail the tick so the worker reopens promptly.
            return false;
        }
        var reply = _rx.RfRead(Slv3Protocol.PageLength * pageCount);
        // A missing/invalid GetDev echo with a healthy handle is a wedged RX
        // MCU (streak -> 0x15 reset); a valid reply listing zero devices is an
        // RF sampling gap and takes the normal merge path.
        if (reply.Length < Slv3Protocol.RecordHeaderLength || reply[0] != Slv3Protocol.UsbSendRf)
        {
            return HandleGetDevFailureLocked();
        }
        _rxFailStreak = 0;

        State.MotherboardPwmPercent = Slv3Protocol.ParseGetDevMoboDuty(reply);

        var count = Slv3Protocol.RecordCount(reply);
        var nowMs = _nowMs();
        for (var i = 0; i < count; i++)
        {
            var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
            if (Slv3Protocol.TryParseRecord(reply, offset, out var record) && record.IsWirelessFan)
            {
                var key = Convert.ToHexString(record.Mac);
                if (!_knownChains.ContainsKey(key))
                {
                    ServiceLog.Info($"[lianli-wireless] chain {key} appeared ({record.FanCount} fan(s), {record.Family})");
                }
                _knownChains[key] = new Slv3KnownChain(record, nowMs);
            }
        }

        List<string>? gone = null;
        foreach (var (key, chain) in _knownChains)
        {
            if (nowMs - chain.LastSeenMs > ChainExpiryMs)
            {
                (gone ??= new List<string>()).Add(key);
            }
        }
        if (gone is not null)
        {
            foreach (var key in gone)
            {
                ServiceLog.Info($"[lianli-wireless] chain {key} dropped ({ChainExpiryMs / 1000}s unseen)");
                _knownChains.Remove(key);
                _lastPwmSent.Remove(key);
            }
        }

        // Page estimate follows the larger of the reply's count and the tracked
        // set, so a partial report can't shrink the next read below the full list.
        _lastRecordCount = Math.Max(count, _knownChains.Count);

        var records = new List<Slv3DeviceRecord>(_knownChains.Count);
        var fans = new List<Slv3FanInfo>(_knownChains.Count);
        foreach (var key in SortedChainKeysLocked())
        {
            var chain = _knownChains[key];
            records.Add(chain.Record);
            fans.Add(ToFanInfo(chain.Record, stale: nowMs - chain.LastSeenMs > ChainStaleMs));
        }
        _lastFanRecords = records;
        State.Fans = fans.ToArray();
        return true;
    }

    // MAC-ordered keys so the surfaced list is stable across polls regardless
    // of dictionary iteration order.
    private List<string> SortedChainKeysLocked()
    {
        var keys = new List<string>(_knownChains.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private bool HandleGetDevFailureLocked()
    {
        _rxFailStreak++;
        if (_rxFailStreak < RxFailStreakForReset)
        {
            // Transient: keep the last-known list and let the next tick retry.
            return true;
        }
        if (_rxResetCount >= MaxRxResetsPerConnection)
        {
            // Out of resets. Streak stays past the threshold, so every further
            // failure lands here and the worker's consecutive-failure path
            // gets its disconnect/reopen.
            return false;
        }
        _rxFailStreak = 0;
        _rxResetCount++;
        ServiceLog.Warn($"[lianli-wireless] {RxFailStreakForReset} consecutive GetDev failures, resetting RX MCU ({_rxResetCount}/{MaxRxResetsPerConnection})");
        if (_rx is not null && _rx.RfSend(Slv3Protocol.BuildResetAnother()))
        {
            _rx.RfRead(Slv3Protocol.UsbPacketSize);
        }
        // Reference sleeps 500 ms after USB_ResetAnother before the next poll.
        Thread.Sleep(RxResetSettleMs);
        return true;
    }

    // This firmware does NOT clear the master MAC on unbind - it clears the slot
    // (rx_type -> 0) and keeps the stale master. So "bound to us" is our master
    // AND a valid slot (1..14); a slot-0 record is unbound even if master matches.
    private bool IsBoundToUsLocked(Slv3DeviceRecord record) =>
        Slv3Protocol.MacEquals(record.MasterMac, _masterMac)
        && record.RxType >= Slv3Protocol.MinSlot
        && record.RxType <= Slv3Protocol.MaxSlot;

    private Slv3FanInfo ToFanInfo(Slv3DeviceRecord record, bool stale) => new()
    {
        Mac = Convert.ToHexString(record.Mac),
        MasterMac = Slv3Protocol.MacIsZero(record.MasterMac) ? "" : Convert.ToHexString(record.MasterMac),
        BoundToUs = IsBoundToUsLocked(record),
        Channel = record.Channel,
        Slot = record.RxType,
        DevType = record.DevType,
        FanType = record.EffectiveFanType,
        FanCount = record.FanCount,
        Rpm = (int[])record.Rpm.Clone(),
        Pwm = (int[])record.Pwm.Clone(),
        EffectIndex = Convert.ToHexString(record.EffectIndex),
        Stale = stale,
    };

    private bool TryFindRecordLocked(byte[] mac, out Slv3DeviceRecord record)
    {
        foreach (var r in _lastFanRecords)
        {
            if (Slv3Protocol.MacEquals(r.Mac, mac))
            {
                record = r;
                return true;
            }
        }
        record = default;
        return false;
    }

    // Sent addressed at the fan's CURRENT (channel, rxType) pipe from the last
    // device-list report, so the dongle steers to it regardless of which master
    // it is presently bound to; the payload's target fields carry what to
    // reconfigure to. Caller holds _lock.
    private bool SendBindFrameLocked(Slv3DeviceRecord record, byte targetSlot)
    {
        if (_tx is null)
        {
            return false;
        }
        var pwm = Slv3Protocol.BuildPwmTuple(DutyTargetsLocked(record.Mac), record.FanCount, record.Family);
        var payload = Slv3Protocol.BuildBind(record.Mac, _masterMac, targetRx: targetSlot, targetChannel: _channel, slot: targetSlot, pwm);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(record.Channel, record.RxType, payload))
        {
            if (!_tx.RfSend(frame))
            {
                return false;
            }
        }
        _lastPwmSent[Convert.ToHexString(record.Mac)] = pwm;
        return true;
    }

    /// <summary>
    /// Whether this tick's bind/PWM keepalive carries anything the chain does
    /// not already have: nothing sent to it yet on this link, or a changed
    /// target tuple.
    ///
    /// Intent only, deliberately. L-Connect's `NeedSyncPwm` also re-sends when
    /// a port's REPORTED duty drifts off target, but this firmware reports
    /// `fans_pwm` as all-zero whatever the commanded duty is, which
    /// `TryParseRecord` then reads as 100 for a spinning fan (Y70, hardware:
    /// commanded 14 and 100 both report 100 and both hold ~590 rpm). A drift
    /// comparison against that can never converge, so it degenerates into the
    /// per-tick re-bind this gate exists to prevent. Caller holds _lock.
    /// </summary>
    private bool NeedsBindFrameLocked(Slv3DeviceRecord record)
    {
        var pwm = Slv3Protocol.BuildPwmTuple(DutyTargetsLocked(record.Mac), record.FanCount, record.Family);
        return !_lastPwmSent.TryGetValue(Convert.ToHexString(record.Mac), out var last)
            || !last.AsSpan().SequenceEqual(pwm);
    }

    // Caller holds _lock.
    private int?[] DutyTargetsLocked(byte[] mac)
    {
        var key = Convert.ToHexString(mac);
        return _dutyTargets.TryGetValue(key, out var targets) ? targets : DefaultDutyTargets;
    }

    // All-zero body: carries no CPU/GPU sensor block (LCD themes are a later
    // phase), which the plan confirms works for a fans-only link.
    private void SendClockHeartbeatLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = new byte[Slv3Protocol.RfPayloadSize];
        Slv3Protocol.WriteRfHeader(payload, Slv3Protocol.RfClockSync, BroadcastMac, _masterMac,
            targetRx: 0, targetChannel: _channel, slot: 0, cmdSeq: NextSeqLocked());
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0, payload))
        {
            _tx.RfSend(frame);
        }
    }

    private byte NextSeqLocked() => _cmdSeq++;

    /// <summary>
    /// Requests a bind to the first free slot (1..14); the connection worker's
    /// tick drives the state machine to completion. Fails if the MAC has never
    /// been seen in a device-list report. A fan already bound to us is left on
    /// its current slot rather than being reassigned a new one.
    /// </summary>
    public bool Bind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            if (IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            var slot = FirstFreeSlotLocked();
            if (slot < 0)
            {
                return false;
            }
            _pending[key] = new Slv3PendingOp(mac, (byte)slot, Unbind: false, PendingOpTickBudget);
            // First frame goes out now; the tick re-sends until the device list
            // confirms, so a request does not wait up to a full tick to start.
            SendBindFrameLocked(existing, (byte)slot);
        }
        return true;
    }

    /// <summary>
    /// Requests a release (slot 0); the connection worker's tick drives the
    /// state machine to completion. Fails if the MAC has never been seen in a
    /// device-list report. A fan already unbound is a no-op.
    /// </summary>
    public bool Unbind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            if (!IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            _pending[key] = new Slv3PendingOp(mac, 0, Unbind: true, PendingOpTickBudget);
            SendBindFrameLocked(existing, 0);
        }
        return true;
    }

    /// <summary>
    /// Arms the TX for wireless-LCD frame traffic: CMD_VIDEO_START then one
    /// prep frame per known device (lian-li-linux ensure_video_mode). L-Connect
    /// sends this before streaming to the SL-LCD screens; the screens' video RF
    /// shares the 2.4 GHz air with this control link (Y70: fan telemetry
    /// collapsed once 3 screens started streaming without it). Idempotent until
    /// the next disconnect.
    /// </summary>
    public bool EnsureVideoMode()
    {
        lock (_lock)
        {
            // Re-arm when chains appeared after the last arming: the LCD loops
            // call this on USB attach, typically before the first GetDev poll
            // has populated the chain list, and un-prepped chains would keep
            // colliding with the video link.
            var deviceCount = Math.Max(1, _knownChains.Count);
            if (_videoModeActive && deviceCount <= _videoModePreppedCount)
            {
                return true;
            }
            if (_tx is null)
            {
                return false;
            }
            if (!_tx.RfSend(Slv3Protocol.BuildVideoStart()))
            {
                return false;
            }
            // Video-start is wire-identical to GetMac(channel 1); drain the
            // reply so it cannot be consumed by a later channel scan.
            _tx.RfRead(Slv3Protocol.UsbPacketSize);
            // Reference gaps: 2 ms after video-start, 1 ms between prep frames.
            Thread.Sleep(2);
            for (var i = 0; i < deviceCount; i++)
            {
                if (!_tx.RfSend(Slv3Protocol.BuildVideoPrep((byte)i, _channel)))
                {
                    return false;
                }
                Thread.Sleep(1);
            }
            _videoModeActive = true;
            _videoModePreppedCount = deviceCount;
            ServiceLog.Info($"[lianli-wireless] video mode armed ({deviceCount} device(s))");
            return true;
        }
    }

    /// <summary>
    /// Sends RF RebootLcd (0x16) at a chain 3x. Recovery attempt for a chain
    /// that beacons header-only records (0 fans, no RPM) while staying
    /// reachable; whether the command reboots the whole chain controller or
    /// only its LCD subsystem is a hardware hypothesis pending bench
    /// verification. Repeats are spaced with _lock released so the tick and
    /// writer are not starved.
    /// </summary>
    public bool ResetChain(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        byte channel, rxType;
        var payloads = new byte[3][];
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record))
            {
                return false;
            }
            channel = record.Channel;
            rxType = record.RxType;
            for (var i = 0; i < payloads.Length; i++)
            {
                payloads[i] = new byte[Slv3Protocol.RfPayloadSize];
                Slv3Protocol.WriteRfHeader(payloads[i], Slv3Protocol.RfRebootChain, record.Mac, _masterMac,
                    targetRx: rxType, targetChannel: channel, slot: rxType, cmdSeq: NextSeqLocked());
            }
        }
        for (var i = 0; i < payloads.Length; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(30);
            }
            if (!SendRfPayload(channel, rxType, payloads[i]))
            {
                return false;
            }
        }
        ServiceLog.Info($"[lianli-wireless] chain reset sent to {macHex}");
        return true;
    }

    /// <summary>Sends a one-shot RF_Select frame so the fan flashes for identification.</summary>
    public bool Identify(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record))
            {
                return false;
            }
            var payload = new byte[Slv3Protocol.RfPayloadSize];
            Slv3Protocol.WriteRfHeader(payload, Slv3Protocol.RfSelect, record.Mac, _masterMac,
                targetRx: record.RxType, targetChannel: record.Channel, slot: record.RxType, cmdSeq: NextSeqLocked());
            foreach (var frame in Slv3Protocol.BuildUsbSendRf(record.Channel, record.RxType, payload))
            {
                if (!_tx.RfSend(frame))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Streams a single animation frame's RGB buffer to a bound fan chain:
    /// builds the TinyUZ-compressed RF_RgbSync packet set and sends the header
    /// packet (index 0) repeated per the selected tier, then each data packet
    /// once, addressed to the fan's current (channel, rxType) pipe.
    /// <paramref name="streaming"/> selects the tier: false = one-shot effect
    /// application (4 header repeats, ~20 ms apart - collided headers lock the
    /// controller up, plans/lianli-wireless-support.md section 2); true = a
    /// continuous frame flow where the next frame supersedes a lost one
    /// (2 repeats, 2 ms apart, the reference's live-stream profile).
    /// Returns false (and sends nothing) if the fan is unknown, not bound to
    /// us, or a send fails partway; <paramref name="effectIndexHex"/> carries
    /// the effect_index actually sent on success, so the caller can compare
    /// it against the fan's next device-list echo to detect a dropped push.
    /// </summary>
    public bool SendRgbFrame(
        string macHex, ReadOnlySpan<RgbColor> leds, int brightnessPercent, int intervalMs, bool streaming, out string effectIndexHex)
    {
        effectIndexHex = "";
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        byte channel, rxType;
        byte[] effectIndex;
        byte[][] packets;
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record) || !IsBoundToUsLocked(record))
            {
                return false;
            }

            var raw = Slv3RgbFrame.BuildFrameBuffer(leds, brightnessPercent);
            byte[] compressed;
            try
            {
                compressed = TinyUz.Compress(raw);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            effectIndex = Slv3RgbFrame.BuildEffectIndex(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            packets = Slv3RgbFrame.BuildPackets(
                record.Mac, _masterMac, effectIndex, compressed, leds.Length, totalFrames: 1, intervalMs);
            channel = record.Channel;
            rxType = record.RxType;
        }

        // The settle gaps run with _lock RELEASED so the 1 s device-list poll
        // (DriveTick) keeps running even with several chains streaming; holding
        // _lock across every gap starves that poll at 2+ bound chains. The
        // repeats are identical, so a keepalive/poll frame slipping into a gap
        // is harmless.
        var repeats = streaming ? StreamingHeaderRepeats : ReliableHeaderRepeats;
        var gapMs = streaming ? StreamingHeaderGapMs : ReliableHeaderGapMs;
        for (var i = 0; i < repeats; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(gapMs);
            }
            if (!SendRfPayload(channel, rxType, packets[0]))
            {
                return false;
            }
        }

        // Data parts carry the reassembly sequence - send them contiguously under
        // one lock so a concurrent frame can't split them.
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            for (var p = 1; p < packets.Length; p++)
            {
                if (!SendRfPayloadLocked(channel, rxType, packets[p]))
                {
                    return false;
                }
            }
        }

        effectIndexHex = Convert.ToHexString(effectIndex);
        return true;
    }

    // Same as SendRfPayloadLocked but takes _lock itself, for callers that pace
    // sends across released-lock gaps (SendRgbFrame's header repeats).
    private bool SendRfPayload(byte channel, byte rxType, byte[] payload)
    {
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            return SendRfPayloadLocked(channel, rxType, payload);
        }
    }

    // Caller holds _lock.
    private bool SendRfPayloadLocked(byte channel, byte rxType, byte[] payload)
    {
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(channel, rxType, payload))
        {
            if (!_tx!.RfSend(frame))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Sets a fan chain's port duty target for the next bind-frame keepalive
    /// (plans/lianli-wireless-support.md section 3): null follows the
    /// motherboard PWM header, otherwise a manual percent (0..100). Takes
    /// effect on the connection worker's next ~1 s DriveTick - the keepalive
    /// already re-sends every tick, so no separate re-assert call is needed.
    /// Returns false for a malformed MAC or a port outside
    /// [0, <see cref="Slv3Protocol.PortsPerRecord"/>).
    /// </summary>
    public bool SetPortDuty(string macHex, int port, int? percent)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        if (port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return false;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            if (!_dutyTargets.TryGetValue(key, out var targets))
            {
                targets = new int?[Slv3Protocol.PortsPerRecord];
                _dutyTargets[key] = targets;
            }
            targets[port] = percent is null ? null : Math.Clamp(percent.Value, 0, 100);
        }
        return true;
    }

    /// <summary>Current duty target for a chain's port (null = unset/motherboard-sync).</summary>
    public int? GetPortDuty(string macHex, int port)
    {
        if (!TryParseMac(macHex, out var mac) || port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return null;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            return _dutyTargets.TryGetValue(key, out var targets) ? targets[port] : null;
        }
    }

    /// <summary>Sets our operating channel; must be the default or an odd value (firmware rejects even). Applied to bound fans on the next tick's re-assert.</summary>
    public bool SetChannel(int channel)
    {
        if (channel != Slv3Protocol.DefaultChannel && (channel < 1 || channel > 39 || channel % 2 == 0))
        {
            return false;
        }
        lock (_lock)
        {
            _channel = (byte)channel;
            State.Channel = channel;
        }
        return true;
    }

    // Slots already used by a fan bound to us, or already claimed by an in-flight
    // bind, are excluded. Caller holds _lock.
    private int FirstFreeSlotLocked()
    {
        var used = new HashSet<int>();
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record))
            {
                used.Add(record.RxType);
            }
        }
        foreach (var op in _pending.Values)
        {
            if (!op.Unbind)
            {
                used.Add(op.TargetSlot);
            }
        }
        for (var slot = Slv3Protocol.MinSlot; slot <= Slv3Protocol.MaxSlot; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    private static bool TryParseMac(string macHex, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(macHex))
        {
            return false;
        }
        var clean = macHex.Replace(":", "").Replace("-", "").Trim();
        if (clean.Length != Slv3Protocol.MacLength * 2)
        {
            return false;
        }
        try
        {
            mac = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisconnectLocked();
        }
    }

    private readonly record struct Slv3PendingOp(byte[] Mac, byte TargetSlot, bool Unbind, int TicksRemaining);

    private readonly record struct Slv3KnownChain(Slv3DeviceRecord Record, long LastSeenMs);
}
