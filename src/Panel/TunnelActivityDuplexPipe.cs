using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Panel;

/// <summary>
/// Transport wrapper for the panel tunnel listener: stamps
/// <see cref="PanelTunnelMonitor.MarkInboundActivity"/> on every read that
/// yields bytes. Connection-level, not request-level, so long-lived WebSocket
/// traffic (keepalive pongs) counts as liveness - a connected panel makes no
/// periodic HTTP requests.
/// </summary>
internal sealed class TunnelActivityDuplexPipe : IDuplexPipe
{
    private readonly IDuplexPipe _inner;

    public TunnelActivityDuplexPipe(IDuplexPipe inner, PanelTunnelMonitor monitor)
    {
        _inner = inner;
        Input = new TunnelActivityPipeReader(inner.Input, monitor);
    }

    public PipeReader Input { get; }
    public PipeWriter Output => _inner.Output;
}

/// <summary>
/// Delegating <see cref="PipeReader"/> that marks tunnel activity when a read
/// completes with a non-empty buffer. Kestrel's HTTP/WS consumers examine to
/// the buffer end before re-awaiting, so a completed read correlates with new
/// inbound bytes rather than re-observing unconsumed data.
/// </summary>
internal sealed class TunnelActivityPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly PanelTunnelMonitor _monitor;

    public TunnelActivityPipeReader(PipeReader inner, PanelTunnelMonitor monitor)
    {
        _inner = inner;
        _monitor = monitor;
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        // A canceled read (Kestrel's CancelPendingRead on shutdown/timeout paths)
        // can re-surface already-examined bytes; only an uncanceled non-empty
        // result implies inbound data.
        if (!result.IsCanceled && !result.Buffer.IsEmpty)
        {
            _monitor.MarkInboundActivity();
        }
        return result;
    }

    public override bool TryRead(out ReadResult result)
    {
        var read = _inner.TryRead(out result);
        if (read && !result.IsCanceled && !result.Buffer.IsEmpty)
        {
            _monitor.MarkInboundActivity();
        }
        return read;
    }

    public override void AdvanceTo(System.SequencePosition consumed) => _inner.AdvanceTo(consumed);

    public override void AdvanceTo(System.SequencePosition consumed, System.SequencePosition examined) =>
        _inner.AdvanceTo(consumed, examined);

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(System.Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask CompleteAsync(System.Exception? exception = null) => _inner.CompleteAsync(exception);
}
