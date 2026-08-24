using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>Direction of a <see cref="LinearRegressionTrend"/> window.</summary>
internal enum TrendDirection
{
    Unknown = 0,
    Up = 1,
    Down = -1,
    Neutral = 2,
}

/// <summary>
/// Counts ticks up to <see cref="Value"/>. With <c>automaticReset</c> the count
/// clears on the tick it fires; without, it keeps firing until reset explicitly.
/// </summary>
internal sealed class ResponseTicks
{
    private readonly bool _automaticReset;
    private int _count;

    public ResponseTicks(bool automaticReset = true) => _automaticReset = automaticReset;

    public int Value { get; set; } = 1;

    public void Reset() => _count = 0;

    public bool Trigger()
    {
        var fired = ++_count >= Value;
        if (_automaticReset && fired)
        {
            Reset();
        }
        return fired;
    }
}

/// <summary>
/// Least-squares slope over a sliding window. Reports Up/Down when the slope
/// across half the window exceeds the tolerance, and accumulates sub-tolerance
/// drift so a slow creep still resolves to a direction eventually.
/// </summary>
internal sealed class LinearRegressionTrend
{
    private readonly int _windowSize;
    private readonly double _halfWindowSize;
    private readonly double _tolerance;
    private readonly double[] _buffer;
    private readonly int _sumX;
    private readonly int _sumXSquared;
    private readonly int _sumXAllSquared;
    private bool _initialized;
    private int _i = -1;
    private double _creep;

    public LinearRegressionTrend(int windowSize, double tolerance)
    {
        // n < 2 divides by zero in the slope denominator.
        _windowSize = Math.Max(2, windowSize);
        _halfWindowSize = _windowSize * 0.5;
        _tolerance = tolerance;
        _buffer = new double[_windowSize];
        for (var x = 0; x < _windowSize; x++)
        {
            _sumX += x;
            _sumXSquared += x * x;
        }
        _sumXAllSquared = _sumX * _sumX;
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _initialized = false;
        _i = -1;
        _creep = 0;
    }

    public TrendDirection Update(double value)
    {
        if (_i == -1 && !_initialized)
        {
            _i++;
            _buffer[0] = value;
            return TrendDirection.Unknown;
        }

        _i = (_i + 1) % _windowSize;
        _buffer[_i] = value;
        var slope = Slope();

        if (!_initialized)
        {
            if (_i == _windowSize - 1)
            {
                _initialized = true;
            }
            return TrendDirection.Unknown;
        }

        if (Math.Abs(slope * _halfWindowSize) > _tolerance)
        {
            return slope < 0 ? TrendDirection.Down : TrendDirection.Up;
        }

        _creep += slope;
        if (Math.Abs(_creep) > _tolerance)
        {
            var direction = _creep < 0 ? TrendDirection.Down : TrendDirection.Up;
            _creep = 0;
            return direction;
        }

        return TrendDirection.Neutral;
    }

    private double Slope()
    {
        var n = _buffer.Length;
        double sumY = 0, sumXy = 0;
        var idx = _i;
        for (var i = 0; i < n; i++)
        {
            idx = ++idx % n;
            var y = _buffer[idx];
            sumY += y;
            sumXy += i * y;
        }
        return (n * sumXy - _sumX * sumY) / (n * _sumXSquared - _sumXAllSquared);
    }
}

/// <summary>
/// Passes a value through only after it has moved at least
/// <see cref="HysteresisValue"/> from the last passed value and held that
/// direction for the response time. Up and down are debounced separately.
/// </summary>
internal sealed class SymmetricalHysteresis
{
    private readonly ResponseTicks _up = new();
    private readonly ResponseTicks _down = new();
    private double _previousValue = double.MinValue;

    public int ResponseTime
    {
        get => _up.Value;
        set { _up.Value = value; _down.Value = value; }
    }

    public double HysteresisValue { get; set; }

    public bool Trigger(double currentValue)
    {
        var up = false;
        var down = false;

        if (currentValue - _previousValue >= HysteresisValue)
        {
            up = _up.Trigger();
            _down.Reset();
        }
        else if (_previousValue - currentValue >= HysteresisValue)
        {
            down = _down.Trigger();
            _up.Reset();
        }
        else
        {
            _up.Reset();
            _down.Reset();
        }

        if (up || down || _previousValue == double.MinValue)
        {
            _previousValue = currentValue;
            return true;
        }
        return false;
    }

    public void Reset()
    {
        _up.Reset();
        _down.Reset();
        _previousValue = double.MinValue;
    }
}

/// <summary>
/// Trigger curve: a two-state latch. Holds the last command until the
/// temperature sits past a threshold for the response time, then steps to that
/// side's speed. Port of FanControl's TriggerFanCurve.
/// </summary>
internal sealed class TriggerCurveState
{
    private readonly ResponseTicks _debounce = new();
    private double? _previousTemp;

    public void Reset()
    {
        _debounce.Reset();
        _previousTemp = null;
    }

    public double? Evaluate(TriggerCurveData cfg, double temp, double? previousCommand, int responseTicks)
    {
        _debounce.Value = Math.Max(1, responseTicks);

        if (previousCommand is null)
        {
            _debounce.Reset();
            return temp > cfg.LoadTemp ? cfg.LoadSpeed : cfg.IdleSpeed;
        }

        if (temp >= cfg.LoadTemp && (_previousTemp is null || _previousTemp < cfg.LoadTemp))
        {
            if (_debounce.Trigger())
            {
                _previousTemp = temp;
                return cfg.LoadSpeed;
            }
            return previousCommand;
        }

        if (temp <= cfg.IdleTemp && (_previousTemp is null || _previousTemp > cfg.IdleTemp))
        {
            if (_debounce.Trigger())
            {
                _previousTemp = temp;
                return cfg.IdleSpeed;
            }
            return previousCommand;
        }

        _debounce.Reset();
        return previousCommand;
    }
}

/// <summary>
/// Auto curve: holds a target temperature. Below the load latch it ramps
/// linearly from (idle, min) to (load, max); at load it steps up by Step and
/// down by half that, each behind its own debounce, using short and long trend
/// windows to decide. Port of FanControl's AutoFanCurve.
/// </summary>
internal sealed class AutoCurveState
{
    private LinearRegressionTrend _shortTrend = new(3, 1.0);
    private LinearRegressionTrend _longTrend = new(5, 1.0);
    private SymmetricalHysteresis _hysteresis = new();
    private ResponseTicks _responseUp = new(automaticReset: false);
    private ResponseTicks _responseDown = new(automaticReset: false);
    private double? _lastLoadCommandTarget;
    private bool _underLoad;
    private int _configuredTicks = -1;

    public void Reset()
    {
        _configuredTicks = -1;
        _lastLoadCommandTarget = null;
        _underLoad = false;
        _hysteresis.Reset();
        _shortTrend.Reset();
        _longTrend.Reset();
        _responseUp.Reset();
        _responseDown.Reset();
    }

    /// <summary>Rebuild the trend windows when the response time changes; their size is baked in at construction.</summary>
    private void Configure(int responseTicks)
    {
        if (_configuredTicks == responseTicks)
        {
            return;
        }
        _configuredTicks = responseTicks;
        _shortTrend = new LinearRegressionTrend(responseTicks * 3, 1.0);
        _longTrend = new LinearRegressionTrend(responseTicks * 5, 1.0);
        _hysteresis = new SymmetricalHysteresis { ResponseTime = responseTicks };
        _responseUp = new ResponseTicks(automaticReset: false) { Value = responseTicks };
        _responseDown = new ResponseTicks(automaticReset: false) { Value = 2 * responseTicks };
    }

    public double? Evaluate(AutoCurveData cfg, double temp, double? previousCommand, int responseTicks)
    {
        Configure(Math.Max(1, responseTicks));

        if (previousCommand is null || previousCommand < 0)
        {
            _lastLoadCommandTarget ??= 0.75 * (cfg.MaxSpeed - cfg.MinSpeed) + cfg.MinSpeed;
            return temp > cfg.LoadTemp ? cfg.MaxSpeed : cfg.MinSpeed;
        }

        var longTrend = _longTrend.Update(temp);
        var shortTrend = _shortTrend.Update(temp);

        if (IsUnderLoad(cfg, temp))
        {
            var target = _lastLoadCommandTarget;
            _underLoad = true;

            if (ShouldStepUp(cfg, temp, shortTrend))
            {
                _responseDown.Reset();
                if (_responseUp.Trigger())
                {
                    target = Math.Clamp(previousCommand.Value + cfg.Step, cfg.MinSpeed, cfg.MaxSpeed);
                }
            }
            else if (ShouldStepDown(cfg, temp, longTrend))
            {
                _responseUp.Reset();
                if (_responseDown.Trigger())
                {
                    target = Math.Clamp(previousCommand.Value - cfg.Step / 2.0, cfg.MinSpeed, cfg.MaxSpeed);
                }
            }
            else
            {
                _responseUp.Reset();
                _responseDown.Reset();
            }

            _lastLoadCommandTarget = target;
            return target;
        }

        if (_underLoad
            && (longTrend == TrendDirection.Neutral || longTrend == TrendDirection.Down)
            && _responseDown.Trigger())
        {
            _underLoad = false;
            _responseDown.Reset();
            _responseUp.Reset();
            _hysteresis.Reset();
        }

        if (!_underLoad)
        {
            if (temp <= cfg.IdleTemp)
            {
                _hysteresis.Reset();
                return cfg.MinSpeed;
            }

            if (_hysteresis.Trigger(temp))
            {
                var span = cfg.LoadTemp - cfg.IdleTemp;
                if (span <= 0)
                {
                    return cfg.MaxSpeed;
                }
                var slope = (cfg.MaxSpeed - cfg.MinSpeed) / span;
                return Math.Clamp(cfg.MinSpeed + slope * (temp - cfg.IdleTemp), cfg.MinSpeed, cfg.MaxSpeed);
            }
        }

        return previousCommand;
    }

    private static bool IsUnderLoad(AutoCurveData cfg, double temp) =>
        temp >= 0.75 * (cfg.LoadTemp - cfg.Deadband - cfg.IdleTemp) + cfg.IdleTemp;

    private static bool ShouldStepUp(AutoCurveData cfg, double temp, TrendDirection shortTrend)
    {
        if (shortTrend == TrendDirection.Up && temp >= cfg.LoadTemp - cfg.Deadband)
        {
            return true;
        }
        return temp > cfg.LoadTemp;
    }

    private static bool ShouldStepDown(AutoCurveData cfg, double temp, TrendDirection longTrend)
    {
        if (longTrend == TrendDirection.Down)
        {
            return true;
        }
        return temp <= cfg.LoadTemp - cfg.Deadband && longTrend == TrendDirection.Neutral;
    }
}
