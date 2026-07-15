using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Corsair.XeneonEdge;

/// <summary>
/// Reads the Xeneon Edge's orientation sensor over its vendor HID interface
/// and applies the matching Windows display rotation, gated on the panel
/// record's AutoOrient preference (null/true = on). Opens its own read
/// handle, independent of any lighting path - the Xeneon Edge has no
/// first-party RGB writer in this codebase (OpenRGB covers its lighting).
/// Modeled on <see cref="Hyte.Keeb.KeebInputWorker"/> (overlapped read loop)
/// and <see cref="Hyte.Keeb.KeebConnectionWorker"/> (USB presence gate).
///
/// Also the single owner of the settings-write/settings-read request/reply
/// exchange (msgid 0x0e/0x0f) exposed to routes via <see cref="ReadSettingsAsync"/>
/// / <see cref="SetControlAsync"/> / <see cref="RestoreColorsAsync"/>. A second
/// concurrent HID handle to the same device is allowed by Windows (each open
/// handle gets its own copy of every interrupt-IN report), but a second
/// concurrent *reader* on THIS <see cref="IHidDevice"/> instance is not: Read()
/// reuses one internal buffer/event pair, so two threads calling it on the same
/// instance corrupt each other's transfer. The only thread that ever calls
/// <c>_reader.Read</c> is this worker's own loop thread, so a settings request
/// from an HTTP thread hands its command to that loop instead of reading for
/// itself: it writes the command (Write uses a separate buffer/event from Read,
/// so it is safe alongside an in-flight Read), records what msgid it is waiting
/// for, and awaits a <see cref="TaskCompletionSource{T}"/> that <see cref="Tick"/>
/// completes the next time it reads a report carrying that msgid. Concurrent
/// settings requests are serialized by <see cref="_settingsSemaphore"/> so only
/// one command is outstanding at a time and a reply can never be misattributed.
/// </summary>
public sealed class XeneonEdgeOrientationWorker : BackgroundService
{
    private const int ReadTimeoutMs = 200;
    private const int RetryDelayMs = 1000;
    // The set ack arrives in ~12ms on the bench but the read loop only checks
    // every ReadTimeoutMs; the settings-block reply is itself ~1s slow.
    private const int SetAckTimeoutMs = 1000;
    private const int SettingsReadTimeoutMs = 2500;
    // One rewrite pass is enough in practice; the second is a backstop.
    private const int RestoreVerifyAttempts = 2;
    // The helper's read loop starts just after Connected fires, so the first
    // attempt usually races it; these cover that gap without pretending a
    // fixed delay is a readiness signal.
    private const int HelperReplayAttempts = 5;
    private const int HelperReplayGapMs = 400;

    private readonly IHidEnumerator _hid;
    private readonly HardwarePresence _presence;
    private readonly PanelDeviceRegistry _registry;
    private readonly IDisplayOrientationProvider _orientation;
    private readonly MultiplexHub _hub;
    private readonly object _applyGate = new();
    private readonly object _readerGate = new();
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _settingsSemaphore = new(1, 1);
    private IHidDevice? _reader;
    private byte _pendingMsgId;
    private TaskCompletionSource<byte[]>? _pendingReply;
    /// <summary>Last code whose apply failed, replayed once the helper arrives.</summary>
    private byte? _unapplied;
#if WINDOWS
    private readonly Nexus.Service.Helper.HelperRegistry? _helpers;
#endif

    public XeneonEdgeOrientationWorker(
        IHidEnumerator hid,
        HardwarePresence presence,
        PanelDeviceRegistry registry,
        IDisplayOrientationProvider orientation,
        MultiplexHub hub
#if WINDOWS
        , Nexus.Service.Helper.HelperRegistry? helpers = null
#endif
        )
    {
        _hid = hid;
        _presence = presence;
        _registry = registry;
        _orientation = orientation;
        _hub = hub;
#if WINDOWS
        _helpers = helpers;
        // The panel reports orientation once, on change. The service opens the
        // HID device seconds before the user-session helper connects, so the
        // first report's apply fails ("no helper connected") and no further
        // report arrives until someone physically turns the panel - leaving the
        // display stuck at whatever Windows booted into. Replay the unapplied
        // code when the helper lands.
        if (_helpers is not null) _helpers.Connected += OnHelperConnected;
#endif
    }

#if WINDOWS
    private void OnHelperConnected(Nexus.Service.Helper.HelperConnection connection)
    {
        byte? code;
        lock (_applyGate) code = _unapplied;
        if (code is null) return;
        // HelperConnection.RunAsync raises Connected from inside
        // registry.Register, BEFORE it starts the read loop that delivers
        // command replies. Applying inline here sends the rpc into a
        // connection nothing is reading yet, so it always times out ("helper
        // rpc failed") and blocks the read loop from starting for the whole
        // timeout. Hand off so Register returns and the loop comes up first.
        _ = Task.Run(() => ReplayWhenHelperServesAsync(code.Value));
    }
#endif


    /// <summary>
    /// Replays the unapplied orientation once the helper can actually serve
    /// the rpc. Connected only means the hello handshake landed; the read loop
    /// that delivers replies starts a moment later, and there is no signal for
    /// it. HandleOrientation keeps _unapplied set whenever an apply fails, so
    /// retry a few times and stop as soon as it clears.
    /// </summary>
    private async Task ReplayWhenHelperServesAsync(byte code)
    {
        for (var attempt = 0; attempt < HelperReplayAttempts; attempt++)
        {
            HandleOrientation(code);
            lock (_applyGate) { if (_unapplied is null) return; }
            try { await Task.Delay(HelperReplayGapMs).ConfigureAwait(false); }
            catch { return; }
        }
        ServiceLog.Warn($"[xeneon-orient] helper replay gave up after {HelperReplayAttempts} attempts (code={code})");
    }

    public override void Dispose()
    {
#if WINDOWS
        if (_helpers is not null) _helpers.Connected -= OnHelperConnected;
#endif
        _settingsSemaphore.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Loop(stoppingToken), stoppingToken);

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool active;
            try
            {
                active = Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[xeneon-orient] tick exception: {ex.GetType().Name}: {ex.Message}");
                CloseReader();
                active = false;
            }
            if (!active)
            {
                try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseReader();
    }

    /// <summary>
    /// One presence-check + read cycle. Public so tests can step it
    /// deterministically. Returns false when the caller should back off
    /// (not present / open failed / device gone) instead of ticking again
    /// immediately - a busy retry otherwise pegs a core.
    /// </summary>
    public bool Tick()
    {
        if (!_presence.UsbPresent(XeneonEdgeProtocol.VendorId, XeneonEdgeProtocol.ProductId))
        {
            CloseReader();
            return false;
        }
        if (_reader is null && !OpenAndArm())
        {
            return false;
        }

        var buf = new byte[XeneonEdgeProtocol.ReportLength];
        var n = _reader!.Read(buf, ReadTimeoutMs);
        if (n == 0) return true; // idle: the read blocked up to ReadTimeoutMs, nothing arrived
        if (n < 0)
        {
            // Device gone: tear down and back off; OpenAndArm re-acquires
            // (and re-arms) when the panel returns.
            CloseReader();
            return false;
        }

        // A report matching an outstanding settings request is delivered to
        // that waiter instead of the orientation parser below - the two
        // msgid ranges (0x11 vs 0x0e/0x0f) never overlap, so an in-flight
        // settings request never blocks orientation reports from applying.
        if (TryDeliverPendingReply(buf, n))
        {
            return true;
        }

        if (XeneonEdgeProtocol.TryParseOrientationReport(buf.AsSpan(0, n), out var code))
        {
            HandleOrientation(code);
        }
        return true;
    }

    private bool TryDeliverPendingReply(byte[] buf, int n)
    {
        TaskCompletionSource<byte[]>? pending;
        byte expectedMsgId;
        lock (_pendingGate)
        {
            pending = _pendingReply;
            expectedMsgId = _pendingMsgId;
        }
        if (pending is null || n < 2 || buf[1] != expectedMsgId) return false;

        lock (_pendingGate)
        {
            if (ReferenceEquals(_pendingReply, pending)) _pendingReply = null;
        }
        pending.TrySetResult(buf.AsSpan(0, n).ToArray());
        return true;
    }

    private bool OpenAndArm()
    {
        HidDeviceInfo? info = null;
        foreach (var i in _hid.Find(XeneonEdgeProtocol.VendorId, XeneonEdgeProtocol.ProductId))
        {
            if (i.UsagePage == XeneonEdgeProtocol.UsagePage && i.Usage == XeneonEdgeProtocol.Usage)
            {
                info = i;
                break;
            }
        }
        if (info is null) return false;

        var device = _hid.Open(info.Path, forInput: true);
        if (device is null) return false;

        // The device reports nothing until armed: one query primes the push
        // stream (immediate reply with the current orientation, then an
        // unsolicited report on every change).
        if (!device.Write(XeneonEdgeProtocol.BuildOrientationQuery()))
        {
            device.Dispose();
            return false;
        }

        lock (_readerGate) { _reader = device; }
        ServiceLog.Info($"[xeneon-orient] reader opened + armed on {info.Path}");
        return true;
    }

    private void HandleOrientation(byte code)
    {
        lock (_applyGate)
        {
            var mapped = XeneonEdgeProtocol.ResolveOrientation(code);
            if (mapped is null)
            {
                ServiceLog.Warn($"[xeneon-orient] unknown sensor code={code}");
                return;
            }

            var found = FindActiveRecord();
            if (found is null)
            {
                // The panel record can appear after auto-promotion; keep the code
                // so the next apply attempt is against the real orientation.
                _unapplied = code;
                ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} (no attached xeneon edge panel)");
                return;
            }
            var (record, displayId) = found.Value;
            if (record.AutoOrient == false)
            {
                _unapplied = null;
                ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} ignored (AutoOrient off) displayId={displayId}");
                return;
            }

            var (ok, error) = _orientation.SetDisplayOrientation(displayId, mapped);
            if (ok)
            {
                _unapplied = null;
                // Settings permanence: same model as the manual
                // /displays/{id}/rotation route (DisplayRoutes.cs).
                _registry.UpdateDisplayOrientation(displayId, mapped);
                PanelTopics.BroadcastPanelDevice(_hub, record.Id);
            }
            else
            {
                _unapplied = code;
            }
            ServiceLog.Info($"[xeneon-orient] code={code} -> {mapped} ok={ok} detail='{error}' displayId={displayId}");
        }
    }

    private (PanelDeviceRecord Record, string DisplayId)? FindActiveRecord()
    {
        foreach (var record in _registry.List())
        {
            if (string.IsNullOrEmpty(record.DisplayId)) continue;
            if (record.Enabled == false) continue;
            if (!string.Equals(record.Capabilities?.Family, KnownPanelDisplays.XeneonEdgeFamily, StringComparison.Ordinal)) continue;
            return (record, record.DisplayId);
        }
        return null;
    }

    private void CloseReader()
    {
        lock (_readerGate)
        {
            try { _reader?.Dispose(); } catch { }
            _reader = null;
        }
        // Fail fast instead of leaving a settings caller waiting out its
        // whole timeout for a reply that can no longer arrive.
        TaskCompletionSource<byte[]>? pending;
        lock (_pendingGate)
        {
            pending = _pendingReply;
            _pendingReply = null;
        }
        pending?.TrySetCanceled();
    }

    /// <summary>
    /// Reads the whole settings block (msgid 0x0e) live from the panel.
    /// Slow (~1s on the bench); null when the device is absent or the read
    /// times out.
    /// </summary>
    public async Task<XeneonEdgeSettingsBlock?> ReadSettingsAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(XeneonEdgeProtocol.BuildSettingsQuery(), XeneonEdgeProtocol.MsgIdSettingsBlock, SettingsReadTimeoutMs, ct)
            .ConfigureAwait(false);
        if (reply is null) return null;
        return XeneonEdgeProtocol.TryParseSettingsBlock(reply, out var block) ? block : null;
    }

    /// <summary>
    /// Writes one control (msgid 0x0f) and returns the value the panel
    /// echoed back in its ack, clamped to the control's documented range.
    /// Null when the device is absent, the write failed, or the ack timed
    /// out or reported failure.
    /// </summary>
    public async Task<int?> SetControlAsync(XeneonEdgeControl control, int value, CancellationToken ct)
    {
        var coords = XeneonEdgeControls.Coords[control];
        var clamped = Math.Clamp(value, coords.Min, coords.Max);
        var command = XeneonEdgeProtocol.BuildSetCommand(coords.Group, coords.Item, (byte)clamped);
        var reply = await RequestAsync(command, XeneonEdgeProtocol.MsgIdSet, SetAckTimeoutMs, ct).ConfigureAwait(false);
        if (reply is null) return null;
        if (!XeneonEdgeProtocol.TryParseSetAck(reply, out var ackGroup, out var ackValue)) return null;
        if (ackGroup != coords.Group) return null;
        return ackValue;
    }

    /// <summary>Restores the panel's factory RGB colors (brightness/backlight/contrast untouched).</summary>
    /// <summary>
    /// Restores every control to its factory value. The panel's own 0xff
    /// command only covers RGB, so each control is written individually.
    ///
    /// The write is then verified against a settings read and any control that
    /// did not land is rewritten. A 0x0f ack means the panel received the
    /// command, not that it committed it: a burst of writes intermittently
    /// leaves some controls at their old value despite every ack arriving
    /// (bench-observed on T1, both a stale-value and a fully-correct outcome
    /// from the identical burst). Verifying is the only reliable signal, so it
    /// is done rather than pacing the writes against a guessed delay.
    /// </summary>
    public async Task<bool> RestoreDefaultsAsync(CancellationToken ct)
    {
        foreach (var (control, value) in XeneonEdgeDefaults.All)
        {
            if (await SetControlAsync(control, value, ct).ConfigureAwait(false) is null) return false;
        }

        for (var attempt = 0; attempt < RestoreVerifyAttempts; attempt++)
        {
            var block = await ReadSettingsAsync(ct).ConfigureAwait(false);
            if (block is null) return false;

            var stale = XeneonEdgeDefaults.All
                .Where(d => XeneonEdgeControls.Read(block.Value, d.Control) != d.Value)
                .ToList();
            if (stale.Count == 0) return true;

            ServiceLog.Info($"[xeneon-orient] restore: {stale.Count} control(s) did not take, rewriting ({string.Join(",", stale.Select(s => s.Control))})");
            foreach (var (control, value) in stale)
            {
                if (await SetControlAsync(control, value, ct).ConfigureAwait(false) is null) return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Writes <paramref name="command"/> and awaits the loop thread's next
    /// matching-msgid read (see <see cref="TryDeliverPendingReply"/>).
    /// Serialized by <see cref="_settingsSemaphore"/> so only one command is
    /// outstanding at a time and a reply can never be misattributed.
    /// </summary>
    private async Task<byte[]?> RequestAsync(byte[] command, byte expectedMsgId, int timeoutMs, CancellationToken ct)
    {
        await _settingsSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingGate)
            {
                _pendingMsgId = expectedMsgId;
                _pendingReply = tcs;
            }

            bool wrote;
            lock (_readerGate)
            {
                wrote = _reader is not null && _reader.Write(command);
            }
            if (!wrote)
            {
                lock (_pendingGate) { if (ReferenceEquals(_pendingReply, tcs)) _pendingReply = null; }
                return null;
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetCanceled());
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                lock (_pendingGate) { if (ReferenceEquals(_pendingReply, tcs)) _pendingReply = null; }
            }
        }
        finally
        {
            _settingsSemaphore.Release();
        }
    }
}
