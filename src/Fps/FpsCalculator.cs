namespace Nexus.Service.Fps;

internal sealed class FpsCalculator
{
    private readonly long[] _presentTicks;
    private int _index;
    private int _count;

    public FpsCalculator(int sampleCount = 50)
    {
        if (sampleCount < 2)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "FPS calculation requires at least two samples.");

        _presentTicks = new long[sampleCount];
    }

    public int Count => _count;
    public double FramesPerSecond { get; private set; }

    public double AddFrameTicks(long timestampTicks)
    {
        _presentTicks[_index] = timestampTicks;
        _index = (_index + 1) % _presentTicks.Length;
        if (_count < _presentTicks.Length)
            _count++;

        if (_count < 2)
            return FramesPerSecond;

        var firstTimestamp = _presentTicks[(_index - _count + _presentTicks.Length) % _presentTicks.Length];
        var lastTimestamp = _presentTicks[(_index - 1 + _presentTicks.Length) % _presentTicks.Length];
        var elapsedSeconds = (lastTimestamp - firstTimestamp) / (double)TimeSpan.TicksPerSecond;
        if (elapsedSeconds <= 0)
            return FramesPerSecond;

        FramesPerSecond = (_count - 1) / elapsedSeconds;
        return FramesPerSecond;
    }

    public void Reset()
    {
        Array.Clear(_presentTicks);
        _index = 0;
        _count = 0;
        FramesPerSecond = 0;
    }
}
