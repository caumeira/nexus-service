using System;
using System.Diagnostics;
using Nexus.Service.Peripherals.PixelFormats;

namespace Nexus.Service.Panel.Streams;

/// <summary>Transport that can turn its frames for glass mounted the wrong way up.</summary>
public interface IOrientablePanelTransport
{
    /// <summary>Supplies the panel record's mount orientation, polled per frame.</summary>
    void BindOrientation(Func<(bool Flip180, bool Mirror)> source);
}

/// <summary>
/// Applies a panel record's mount orientation to a BGRA frame on its way to the glass.
/// Doing it here rather than in the renderer keeps the content canonical everywhere else -
/// the editor, the device-page preview and the screenshot all stay upright.
/// </summary>
public sealed class PanelOrientationFilter
{
    /// <summary>How stale the cached orientation may get; reading it clones the panel record.</summary>
    private const long TtlMs = 500;

    private Func<(bool Flip180, bool Mirror)>? _source;
    private byte[] _oriented = Array.Empty<byte>();
    private (bool Flip180, bool Mirror) _current;
    private long _readMs = long.MinValue;

    public void Bind(Func<(bool Flip180, bool Mirror)> source)
    {
        _source = source;
        _readMs = long.MinValue;
    }

    /// <summary>Returns the frame turned as the record asks, or the input when nothing to do.</summary>
    public ReadOnlySpan<byte> Apply(ReadOnlySpan<byte> frame, int width, int height)
    {
        if (_source is null)
        {
            return frame;
        }
        var nowMs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        if (nowMs - _readMs >= TtlMs)
        {
            _readMs = nowMs;
            try { _current = _source(); }
            catch { _current = default; }
        }
        if (BgraOrientation.IsIdentity(_current.Flip180, _current.Mirror))
        {
            return frame;
        }
        if (width <= 0 || height <= 0 || width * height * 4 != frame.Length)
        {
            // Geometry moved under us mid-frame; orienting against a stale size would tear.
            return frame;
        }
        if (_oriented.Length != frame.Length)
        {
            _oriented = new byte[frame.Length];
        }
        BgraOrientation.Apply(frame, width, height, _current.Flip180, _current.Mirror, _oriented);
        return _oriented;
    }
}
