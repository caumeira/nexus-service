using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Singleton coordinator for the SLV3 wireless link: owns the TX + RX dongle
/// transports, the master identity, and the bind/unbind state machine (see
/// plans/lianli-wireless-support.md section 1.7). Phase 1 scope: discovery,
/// bind/unbind/identify, channel. RGB/PWM/LCD are later phases.
/// All hardware I/O and shared state are guarded by one lock: the connection
/// worker's periodic tick and route-driven bind/unbind/identify calls both touch it.
/// </summary>
public sealed class Slv3Hub : IDisposable
{
    private static readonly byte[] BroadcastMac = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] DefaultPwm =
    {
        Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.PwmFollowMotherboard,
        Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.PwmFollowMotherboard,
    };

    private readonly ISlv3Discovery _discovery;
    private readonly Func<Slv3PortInfo, ISlv3Transport> _transportFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slv3PendingOp> _pending = new(StringComparer.Ordinal);
    private List<Slv3DeviceRecord> _lastFanRecords = new();

    private ISlv3Transport? _tx;
    private ISlv3Transport? _rx;
    private byte[] _masterMac = new byte[Slv3Protocol.MacLength];
    private byte _channel = Slv3Protocol.DefaultChannel;
    private byte _cmdSeq;
    private bool _disposed;

    public Slv3Hub(ISlv3Discovery discovery, Func<Slv3PortInfo, ISlv3Transport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public Slv3State State { get; } = new();

    public bool IsConnected => _tx is { IsOpen: true } && _rx is { IsOpen: true };

    /// <summary>
    /// True when both dongles are enumerable, without opening them. WinUSB is
    /// exclusive-open, so the worker must stop a conflicting L-Connect before
    /// <see cref="EnsureConnected"/> or CreateFileW fails with a sharing violation.
    /// </summary>
    public bool DonglesPresent()
    {
        if (_disposed)
        {
            return false;
        }
        var hasTx = false;
        var hasRx = false;
        foreach (var port in _discovery.Discover())
        {
            hasTx |= port.Role == Slv3DongleRole.Tx;
            hasRx |= port.Role == Slv3DongleRole.Rx;
        }
        return hasTx && hasRx;
    }

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
        _lastFanRecords = new List<Slv3DeviceRecord>();
    }

    private bool MasterInitLocked()
    {
        if (_tx is null)
        {
            return false;
        }
        if (!_tx.RfSend(Slv3Protocol.BuildGetMac(_channel)))
        {
            return false;
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        if (!Slv3Protocol.TryParseGetMac(reply, out var mac, out _, out var fw))
        {
            return false;
        }
        _masterMac = mac;
        State.MasterMac = Convert.ToHexString(mac);
        State.TxFirmwareVersion = fw;
        State.Channel = _channel;
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
                if (IsBoundToUsLocked(record))
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
            return true;
        }
    }

    private void ResolvePendingLocked()
    {
        List<string>? resolved = null;
        foreach (var (key, op) in _pending)
        {
            if (!TryFindRecordLocked(op.Mac, out var record))
            {
                continue;
            }
            var done = op.Unbind
                ? !IsBoundToUsLocked(record)
                : IsBoundToUsLocked(record);
            if (done)
            {
                (resolved ??= new List<string>()).Add(key);
            }
        }
        if (resolved is null)
        {
            return;
        }
        foreach (var key in resolved)
        {
            _pending.Remove(key);
        }
    }

    private bool RefreshDeviceListLocked()
    {
        if (_rx is null)
        {
            return false;
        }
        // The device cap (<=10 fans, <=3 Strimer, <=1 WaterBlock, etc.) fits one page.
        const byte pageCount = 1;
        if (!_rx.RfSend(Slv3Protocol.BuildGetDev(pageCount)))
        {
            return false;
        }
        var reply = _rx.RfRead(Slv3Protocol.PageLength * pageCount);
        var count = Slv3Protocol.RecordCount(reply);
        var records = new List<Slv3DeviceRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
            if (Slv3Protocol.TryParseRecord(reply, offset, out var record) && record.IsWirelessFan)
            {
                records.Add(record);
            }
        }

        if (records.Count != _lastFanRecords.Count)
        {
            ServiceLog.Info($"[lianli-wireless] device list: {records.Count} fan chain(s)");
        }
        _lastFanRecords = records;
        var fans = new Slv3FanInfo[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            fans[i] = ToFanInfo(records[i]);
        }
        State.Fans = fans;
        return true;
    }

    // This firmware does NOT clear the master MAC on unbind - it clears the slot
    // (rx_type -> 0) and keeps the stale master. So "bound to us" is our master
    // AND a valid slot (1..14); a slot-0 record is unbound even if master matches.
    private bool IsBoundToUsLocked(Slv3DeviceRecord record) =>
        Slv3Protocol.MacEquals(record.MasterMac, _masterMac)
        && record.RxType >= Slv3Protocol.MinSlot
        && record.RxType <= Slv3Protocol.MaxSlot;

    private Slv3FanInfo ToFanInfo(Slv3DeviceRecord record) => new()
    {
        Mac = Convert.ToHexString(record.Mac),
        MasterMac = Slv3Protocol.MacIsZero(record.MasterMac) ? "" : Convert.ToHexString(record.MasterMac),
        BoundToUs = IsBoundToUsLocked(record),
        Channel = record.Channel,
        Slot = record.RxType,
        DevType = record.DevType,
        FanType = record.PrimaryFanType,
        FanCount = record.FanCount,
        Rpm = (int[])record.Rpm.Clone(),
        Pwm = (int[])record.Pwm.Clone(),
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
        var payload = Slv3Protocol.BuildBind(record.Mac, _masterMac, targetRx: targetSlot, targetChannel: _channel, slot: targetSlot, DefaultPwm);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(record.Channel, record.RxType, payload))
        {
            if (!_tx.RfSend(frame))
            {
                return false;
            }
        }
        return true;
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
            _pending[key] = new Slv3PendingOp(mac, (byte)slot, Unbind: false);
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
            _pending[key] = new Slv3PendingOp(mac, 0, Unbind: true);
        }
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

    private readonly record struct Slv3PendingOp(byte[] Mac, byte TargetSlot, bool Unbind);
}
