using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Diagnostics.Report;

/// <summary>Absolute-coordinate path operation, already in the source's own unit space.</summary>
internal abstract record PathOp
{
    public sealed record MoveTo(double X, double Y) : PathOp;
    public sealed record LineTo(double X, double Y) : PathOp;
    public sealed record CurveTo(double X1, double Y1, double X2, double Y2, double X3, double Y3) : PathOp;
    public sealed record ClosePath : PathOp;
}

/// <summary>
/// Parses the subset of SVG path data potrace emits: absolute moveto (M),
/// relative lineto/curveto/moveto (l, c, m) and closepath (Z/z), each
/// optionally repeating its coordinate group without repeating the command
/// letter (standard SVG path shorthand). Any other command letter throws -
/// this parser is scoped to exactly what potrace produces, not general SVG.
/// </summary>
internal static class PotracePathParser
{
    private const string SupportedCommands = "MmLlCcZz";

    public static IReadOnlyList<PathOp> Parse(string d)
    {
        var ops = new List<PathOp>();
        var i = 0;
        var n = d.Length;
        double curX = 0, curY = 0, startX = 0, startY = 0;

        void SkipSeparators()
        {
            while (i < n && (char.IsWhiteSpace(d[i]) || d[i] == ','))
            {
                i++;
            }
        }

        bool TryPeekNumberStart()
        {
            SkipSeparators();
            return i < n && (d[i] == '+' || d[i] == '-' || d[i] == '.' || char.IsDigit(d[i]));
        }

        double ReadNumber()
        {
            SkipSeparators();
            var start = i;
            if (i < n && (d[i] == '+' || d[i] == '-'))
            {
                i++;
            }
            while (i < n && char.IsDigit(d[i]))
            {
                i++;
            }
            if (i < n && d[i] == '.')
            {
                i++;
                while (i < n && char.IsDigit(d[i]))
                {
                    i++;
                }
            }
            if (start == i)
            {
                throw new FormatException($"expected a number at position {i} in path data");
            }
            return double.Parse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        while (true)
        {
            SkipSeparators();
            if (i >= n)
            {
                break;
            }

            var cmd = d[i];
            if (SupportedCommands.IndexOf(cmd, StringComparison.Ordinal) < 0)
            {
                throw new NotSupportedException($"unsupported path command '{cmd}'");
            }
            i++;

            switch (cmd)
            {
                case 'M':
                case 'm':
                {
                    var first = true;
                    while (first || TryPeekNumberStart())
                    {
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (cmd == 'm')
                        {
                            x += curX;
                            y += curY;
                        }
                        curX = x;
                        curY = y;
                        if (first)
                        {
                            startX = curX;
                            startY = curY;
                            ops.Add(new PathOp.MoveTo(curX, curY));
                        }
                        else
                        {
                            ops.Add(new PathOp.LineTo(curX, curY));
                        }
                        first = false;
                    }
                    break;
                }
                case 'L':
                case 'l':
                    while (TryPeekNumberStart())
                    {
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (cmd == 'l')
                        {
                            x += curX;
                            y += curY;
                        }
                        curX = x;
                        curY = y;
                        ops.Add(new PathOp.LineTo(curX, curY));
                    }
                    break;
                case 'C':
                case 'c':
                    while (TryPeekNumberStart())
                    {
                        var x1 = ReadNumber();
                        var y1 = ReadNumber();
                        var x2 = ReadNumber();
                        var y2 = ReadNumber();
                        var x3 = ReadNumber();
                        var y3 = ReadNumber();
                        if (cmd == 'c')
                        {
                            x1 += curX; y1 += curY;
                            x2 += curX; y2 += curY;
                            x3 += curX; y3 += curY;
                        }
                        ops.Add(new PathOp.CurveTo(x1, y1, x2, y2, x3, y3));
                        curX = x3;
                        curY = y3;
                    }
                    break;
                case 'Z':
                case 'z':
                    ops.Add(new PathOp.ClosePath());
                    curX = startX;
                    curY = startY;
                    break;
            }
        }

        return ops;
    }
}
