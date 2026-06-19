using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Thread-safe: key presses arrive from the input worker thread; Render() is called from the frame writer thread.
/// </summary>
public sealed class KeebReactiveRenderer
{
    // HYTE HLine/VLine parameters (matches HYTEKeyboardController animation constants).
    private const int TotalLineSize = 5;
    private const int LineMovement = 3; // TotalLineSize / 2 + 1
    private const int KeyboardXAxisCounts = 21;
    private const int RippleInnerSize = 6;

    // 12-color rainbow palette used by the Ripple animation (matches HYTE's colorList).
    private static readonly RgbColor[] RippleColors = new RgbColor[12]
    {
        new(255,   0,   0),
        new(255, 127,   0),
        new(255, 255,   0),
        new(127, 255,   0),
        new(  0, 255,   0),
        new(  0, 255, 127),
        new(  0, 255, 255),
        new(  0, 127, 255),
        new(  0,   0, 255),
        new(127,   0, 255),
        new(255,   0, 255),
        new(255,   0, 127),
    };

    // Compile-time mapping: (fwRow, fwCol) -> (ledIndex, gridY, gridX).
    // ledIndex = Array.IndexOf(KeebLayout.KeyWireValues, commandsIndex - 1).
    private static readonly Dictionary<(int, int), (int LedIndex, int GridY, int GridX)> FwToLed;

    // (gridY, gridX) -> ledIndex for SingleKey and neighbor lookups.
    private static readonly Dictionary<(int, int), int> GridToLedIndex;

    // Flat array of (gridY, gridX, ledIndex) for HLine/VLine/Ripple iteration.
    private static readonly (int GridY, int GridX, int LedIndex)[] GridEntries;

    static KeebReactiveRenderer()
    {
        // Table: (fwRow, fwCol, gridY, gridX, commandsIndex)
        (int FwRow, int FwCol, int GridY, int GridX, int CommandsIndex)[] table =
        {
            (0,  0, 3,  1,   1), // ESC
            (0,  2, 3,  3,   3), // F1
            (0,  3, 3,  4,   4), // F2
            (0,  4, 3,  5,   5), // F3
            (0,  5, 3,  6,   6), // F4
            (0,  6, 3,  8,   7), // F5
            (0,  7, 3,  9,   8), // F6
            (0,  8, 3, 10,   9), // F7
            (0,  9, 3, 11,  10), // F8
            (0, 10, 3, 13,  11), // F9
            (0, 11, 3, 14,  12), // F10
            (0, 12, 3, 15,  13), // F11
            (0, 13, 3, 16,  14), // F12
            (0, 14, 3, 17,  15), // Pause
            (0, 15, 3, 18,  16), // ScrLk
            (0, 16, 3, 19,  17), // PrnSrc
            (1,  0, 4,  1,  22), // Tilde
            (1,  1, 4,  2,  23), // 1
            (1,  2, 4,  3,  24), // 2
            (1,  3, 4,  4,  25), // 3
            (1,  4, 4,  5,  26), // 4
            (1,  5, 4,  6,  27), // 5
            (1,  6, 4,  7,  28), // 6
            (1,  7, 4,  8,  29), // 7
            (1,  8, 4,  9,  30), // 8
            (1,  9, 4, 10,  31), // 9
            (1, 10, 4, 11,  32), // 0
            (1, 11, 4, 12,  33), // Hyphen
            (1, 12, 4, 13,  34), // Plus
            (1, 13, 4, 15,  35), // Backspace
            (1, 14, 4, 17,  36), // Insert
            (1, 15, 4, 18,  37), // Home
            (1, 16, 4, 19,  38), // PageUp
            (2,  0, 5,  1,  43), // Tab
            (2,  1, 5,  3,  44), // Q
            (2,  2, 5,  4,  45), // W
            (2,  3, 5,  5,  46), // E
            (2,  4, 5,  6,  47), // R
            (2,  5, 5,  7,  48), // T
            (2,  6, 5,  8,  49), // Y
            (2,  7, 5,  9,  50), // U
            (2,  8, 5, 10,  51), // I
            (2,  9, 5, 11,  52), // O
            (2, 10, 5, 12,  53), // P
            (2, 11, 5, 13,  54), // [
            (2, 12, 5, 14,  55), // ]
            (2, 13, 5, 15,  56), // Backslash
            (2, 14, 5, 17,  57), // Del
            (2, 15, 5, 18,  58), // End
            (2, 16, 5, 19,  59), // PageDown
            (3,  0, 6,  1,  64), // CapsLock
            (3,  1, 6,  3,  65), // A
            (3,  2, 6,  4,  66), // S
            (3,  3, 6,  5,  67), // D
            (3,  4, 6,  6,  68), // F
            (3,  5, 6,  7,  69), // G
            (3,  6, 6,  8,  70), // H
            (3,  7, 6,  9,  71), // J
            (3,  8, 6, 10,  72), // K
            (3,  9, 6, 11,  73), // L
            (3, 10, 6, 12,  74), // ;
            (3, 11, 6, 13,  75), // '
            (3, 13, 6, 15,  77), // Enter
            (4,  0, 7,  2,  85), // LShift
            (4,  2, 7,  4,  87), // Z
            (4,  3, 7,  5,  88), // X
            (4,  4, 7,  6,  89), // C
            (4,  5, 7,  7,  90), // V
            (4,  6, 7,  8,  91), // B
            (4,  7, 7,  9,  92), // N
            (4,  8, 7, 10,  93), // M
            (4,  9, 7, 11,  94), // ,
            (4, 10, 7, 12,  95), // .
            (4, 11, 7, 13,  96), // /
            (4, 13, 7, 15,  98), // RShift
            (4, 15, 7, 18, 100), // Up
            (5,  0, 8,  1, 106), // LCtrl
            (5,  1, 8,  2, 107), // LWin
            (5,  2, 8,  3, 108), // LAlt
            (5,  6, 8,  7, 112), // Space
            (5, 10, 8, 12, 116), // RAlt
            (5, 11, 8, 13, 117), // Fn
            (5, 12, 8, 14, 118), // Menu
            (5, 13, 8, 15, 119), // RCtrl
            (5, 14, 8, 17, 120), // Left
            (5, 15, 8, 18, 121), // Down
            (5, 16, 8, 19, 122), // Right
        };

        FwToLed = new Dictionary<(int, int), (int, int, int)>(table.Length);
        GridToLedIndex = new Dictionary<(int, int), int>(table.Length);
        var gridEntries = new List<(int, int, int)>(table.Length);
        var keyWireValues = KeebLayout.KeyWireValues;
        foreach (var (fwRow, fwCol, gridY, gridX, commandsIndex) in table)
        {
            var wireSlot = commandsIndex - 1;
            var ledIndex = Array.IndexOf(keyWireValues, wireSlot);
            if (ledIndex < 0) continue;
            FwToLed[(fwRow, fwCol)] = (ledIndex, gridY, gridX);
            GridToLedIndex[(gridY, gridX)] = ledIndex;
            gridEntries.Add((gridY, gridX, ledIndex));
        }
        GridEntries = gridEntries.ToArray();
    }

    private record struct Reaction(int GridY, int GridX, int Frame, string Mode);

    private readonly object _lock = new();
    private readonly Queue<(int FwRow, int FwCol)> _pending = new();
    private readonly List<Reaction> _active = new();
    private readonly RgbColor?[] _renderBuf = new RgbColor?[KeebLayout.KeyLedCount];

    // Snapshotted settings, written from Configure() on the frame writer thread.
    private string _mode = "SingleKey";
    private RgbColor _color = new(255, 0, 0);

    public void Configure(bool enabled, string mode, RgbColor color)
    {
        var modeChanged = mode != _mode;
        _mode = mode;
        _color = color;

        if (modeChanged)
        {
            lock (_lock)
            {
                _active.Clear();
                _pending.Clear();
            }
        }
    }

    public void IngestKeyPress(int fwRow, int fwCol)
    {
        lock (_lock)
        {
            _pending.Enqueue((fwRow, fwCol));
        }
    }

    public RgbColor?[]? Render()
    {
        lock (_lock)
        {
            DrainPending();

            if (_active.Count == 0) return null;

            Array.Clear(_renderBuf, 0, _renderBuf.Length);
            RenderActive();
            AdvanceAndPrune();
        }

        return _renderBuf;
    }

    private void DrainPending()
    {
        while (_pending.Count > 0)
        {
            var (fwRow, fwCol) = _pending.Dequeue();
            if (!FwToLed.TryGetValue((fwRow, fwCol), out var info)) continue;
            var (_, gridY, gridX) = info;

            var mode = _mode;
            if (mode == "SingleKey")
            {
                // Retrigger if the same key already has an active reaction.
                var found = false;
                for (var i = 0; i < _active.Count; i++)
                {
                    if (_active[i].GridY == gridY && _active[i].GridX == gridX && _active[i].Mode == mode)
                    {
                        _active[i] = _active[i] with { Frame = 0 };
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    _active.Add(new Reaction(gridY, gridX, 0, mode));
                }
            }
            else
            {
                _active.Add(new Reaction(gridY, gridX, 0, mode));
            }
        }
    }

    private void RenderActive()
    {
        foreach (var r in _active)
        {
            switch (r.Mode)
            {
                case "SingleKey":
                    RenderSingleKey(r);
                    break;
                case "HorizontalLine":
                    RenderHorizontalLine(r);
                    break;
                case "VerticalLine":
                    RenderVerticalLine(r);
                    break;
                case "Ripple":
                    RenderRipple(r);
                    break;
            }
        }
    }

    private void AdvanceAndPrune()
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var r = _active[i];
            var nextFrame = r.Frame + 1;
            var done = r.Mode switch
            {
                "SingleKey" => nextFrame >= 10,
                "HorizontalLine" => nextFrame >= HLineMaxMovement(r.GridX),
                "VerticalLine" => nextFrame >= VLineMaxMovement(r.GridY),
                "Ripple" => nextFrame >= RippleSize(r.GridX),
                _ => true,
            };

            if (done)
            {
                _active.RemoveAt(i);
            }
            else
            {
                _active[i] = r with { Frame = nextFrame };
            }
        }
    }

    private void RenderSingleKey(Reaction r)
    {
        if (!GridToLedIndex.TryGetValue((r.GridY, r.GridX), out var ledIndex)) return;
        // Fade: 10 steps, frame 0 = full brightness (step 10), frame 9 = step 1.
        var step = 10 - r.Frame;
        var brightness = step / 10.0;
        _renderBuf[ledIndex] = ScaleColor(_color, brightness);
    }

    private void RenderHorizontalLine(Reaction r)
    {
        var f = r.Frame;
        // At frame f the lit set is columns where abs(c - originX) in [max(0, f - (LineMovement-1)), f].
        var minDist = Math.Max(0, f - (LineMovement - 1));
        var maxDist = f;

        foreach (var (gridY, gridX, ledIndex) in GridEntries)
        {
            if (gridY != r.GridY) continue;
            var dist = Math.Abs(gridX - r.GridX);
            if (dist < minDist || dist > maxDist) continue;
            _renderBuf[ledIndex] = _color;
        }
    }

    private void RenderVerticalLine(Reaction r)
    {
        var f = r.Frame;
        var minDist = Math.Max(0, f - (LineMovement - 1));
        var maxDist = f;

        foreach (var (gridY, gridX, ledIndex) in GridEntries)
        {
            if (gridX != r.GridX) continue;
            var dist = Math.Abs(gridY - r.GridY);
            if (dist < minDist || dist > maxDist) continue;
            _renderBuf[ledIndex] = _color;
        }
    }

    private void RenderRipple(Reaction r)
    {
        var f = r.Frame;
        // Visible ring: cells where (f - RippleInnerSize) <= dist < f.
        var ringMin = f >= RippleInnerSize ? (double)(f - RippleInnerSize) : 0.0;
        var ringMax = (double)f;

        foreach (var (gridY, gridX, ledIndex) in GridEntries)
        {
            var dy = gridY - r.GridY;
            var dx = gridX - r.GridX;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < ringMin || dist >= ringMax) continue;

            // Color index cycles through the rainbow as the ring expands.
            const double colorSpeed = 1.5 / 4.0;
            var colorIdx = (int)(dist - f * colorSpeed);
            colorIdx = ((colorIdx % 12) + 12) % 12;
            _renderBuf[ledIndex] = RippleColors[colorIdx];
        }
    }

    private static RgbColor ScaleColor(RgbColor c, double brightness)
    {
        if (brightness <= 0) return new RgbColor(0, 0, 0);
        if (brightness >= 1) return c;
        return new RgbColor(
            (byte)(c.R * brightness),
            (byte)(c.G * brightness),
            (byte)(c.B * brightness));
    }

    private static int HLineMaxMovement(int originX)
        => Math.Max(KeyboardXAxisCounts - originX + TotalLineSize, originX + TotalLineSize);

    private static int VLineMaxMovement(int originY)
    {
        // Grid Y range is 3..8 (6 rows).
        const int keyboardYAxisCounts = 9; // one past max gridY of 8
        return Math.Max(keyboardYAxisCounts - originY + TotalLineSize, originY + TotalLineSize);
    }

    private static int RippleSize(int originX)
        => Math.Max((originX + 1) * 2, (KeyboardXAxisCounts - originX + 1) * 2);
}
