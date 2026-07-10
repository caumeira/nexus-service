using System;
using System.Collections.Generic;

namespace Nexus.Service.Panel.Streams;

public enum StreamSessionState
{
    WaitingForIngest,
    Live,
    Resync,
    Closed,
}

/// <summary>
/// One live stream instance: the frame queue between the ingest reader and
/// the paced writer, plus the resync latch. SessionIds are boot-scoped and
/// re-minted on config change or re-attach, so a stale overlay can never
/// write into a new session.
/// </summary>
public sealed class StreamSession
{
    private readonly object _lock = new();
    private readonly Queue<StreamFrame> _queue = new();
    private readonly int _maxQueuedFrames;
    private bool _waitingForIdr = true;
    private bool _ingestBound;
    private bool _transportUp;
    private bool _closed;

    public StreamSession(string sessionId, StreamedPanelDeviceInfo info, string panelDeviceId)
    {
        SessionId = sessionId;
        Info = info;
        PanelDeviceId = panelDeviceId;
        // 2s of frames: enough to absorb a transport blip, small enough that
        // a stalled device resumes at a near-live IDR instead of replaying.
        _maxQueuedFrames = Math.Max(30, info.Profile.Fps * 2);
    }

    public string SessionId { get; }
    public StreamedPanelDeviceInfo Info { get; }
    public string PanelDeviceId { get; }

    public StreamSessionState State
    {
        get
        {
            lock (_lock)
            {
                if (_closed) return StreamSessionState.Closed;
                if (!_transportUp) return StreamSessionState.Resync;
                return _ingestBound ? StreamSessionState.Live : StreamSessionState.WaitingForIngest;
            }
        }
    }

    public bool Closed
    {
        get { lock (_lock) return _closed; }
    }

    public void Enqueue(StreamFrame frame)
    {
        lock (_lock)
        {
            if (_closed) return;
            _queue.Enqueue(frame);
            if (_queue.Count > _maxQueuedFrames)
                TrimToNewestIdrLocked();
        }
    }

    /// <summary>
    /// Applies one pacing tick: drops a resync prefix (only ever ending at an
    /// IDR), then dequeues the frames to put on the wire this tick.
    /// </summary>
    public IReadOnlyList<StreamFrame> DequeueForTick()
    {
        lock (_lock)
        {
            var decision = PacingPolicy.Decide(_queue.Count, FramesUntilIdrLocked(), _waitingForIdr);
            for (var i = 0; i < decision.DropCount; i++) _queue.Dequeue();
            if (decision.ClearWaitingForIdr) _waitingForIdr = false;
            if (decision.SendCount == 0) return Array.Empty<StreamFrame>();
            var send = new List<StreamFrame>(decision.SendCount);
            for (var i = 0; i < decision.SendCount && _queue.Count > 0; i++)
                send.Add(_queue.Dequeue());
            return send;
        }
    }

    /// <summary>Transport (re)opened: nothing may hit the wire before an IDR.</summary>
    public void RequireIdrResync()
    {
        lock (_lock) _waitingForIdr = true;
    }

    /// <summary>
    /// New ingest connection: the previous encoder's tail is a dead stream,
    /// and a fresh encoder always opens with SPS/PPS+IDR.
    /// </summary>
    public void ResetForNewIngest()
    {
        lock (_lock)
        {
            _queue.Clear();
            _waitingForIdr = true;
            _ingestBound = true;
        }
    }

    public void SetIngestBound(bool bound)
    {
        lock (_lock) _ingestBound = bound;
    }

    public void SetTransportUp(bool up)
    {
        lock (_lock) _transportUp = up;
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            _queue.Clear();
        }
    }

    internal int QueueDepthForTest
    {
        get { lock (_lock) return _queue.Count; }
    }

    private int FramesUntilIdrLocked()
    {
        var i = 0;
        foreach (var frame in _queue)
        {
            if (frame.IsIdr) return i;
            i++;
        }
        return -1;
    }

    // Keeps only the suffix starting at the newest IDR so the decoder can
    // resume without a mid-GOP gap; arms the resync latch when everything
    // queued is mid-GOP.
    private void TrimToNewestIdrLocked()
    {
        var frames = _queue.ToArray();
        var newestIdr = -1;
        for (var i = frames.Length - 1; i >= 0; i--)
        {
            if (frames[i].IsIdr) { newestIdr = i; break; }
        }
        _queue.Clear();
        if (newestIdr < 0)
        {
            _waitingForIdr = true;
            return;
        }
        for (var i = newestIdr; i < frames.Length; i++)
            _queue.Enqueue(frames[i]);
    }
}
