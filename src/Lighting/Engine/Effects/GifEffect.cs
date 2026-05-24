using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Lighting.Engine.Effects;

public sealed class GifEffect : IEffect
{
    public string Name => "gif";
    private readonly List<byte[]> _frames = new();
    private readonly List<int> _delaysMs = new();
    private readonly List<(int w, int h)> _frameSizes = new();
    private byte[] _colorTable = Array.Empty<byte>();
    private int _currentFrame;
    private double _lastAdvanceMs;
    public const long MaxFileSizeBytes = 50 * 1024 * 1024;
    public const int MaxFrames = 500;

    public GifEffect(string gifPath)
    { try { LoadGif(gifPath); } catch (Exception ex) { Console.Error.WriteLine($"[gif-effect] {ex.Message}"); } }

    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        if (_frames.Count == 0)
        { canvas.Fill(30, 30, 40); return; }
        var delay = _delaysMs.Count > _currentFrame ? _delaysMs[_currentFrame] : 100;
        if (tickMs - _lastAdvanceMs >= delay)
        { _currentFrame = (_currentFrame + 1) % _frames.Count; _lastAdvanceMs = tickMs; }
        var pixels = _frames[_currentFrame];
        var (fw, fh) = _frameSizes[_currentFrame];
        var ct = _colorTable;
        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                var sx = x * fw / canvas.Width;
                var sy = y * fh / canvas.Height;
                sx = Math.Clamp(sx, 0, fw - 1);
                sy = Math.Clamp(sy, 0, fh - 1);
                var idx = sy * fw + sx;
                if (idx < pixels.Length)
                {
                    var ci = pixels[idx] * 3;
                    if (ci + 2 < ct.Length)
                    {
                        canvas.SetPixel(x, y, ct[ci], ct[ci + 1], ct[ci + 2]);
                    }
                }
            }
        }
    }

    public void Dispose() { }

    private void LoadGif(string gifPath)
    {
        if (!File.Exists(gifPath))
        {
            return;
        }

        if (new FileInfo(gifPath).Length > MaxFileSizeBytes)
        {
            return;
        }

        var data = File.ReadAllBytes(gifPath);
        if (data.Length < 13)
        {
            return;
        }

        var sig = System.Text.Encoding.ASCII.GetString(data, 0, 6);
        if (sig != "GIF87a" && sig != "GIF89a")
        {
            return;
        }

        var packed = data[10];
        var hasGct = (packed & 0x80) != 0;
        var gctSize = hasGct ? 3 * (1 << ((packed & 0x07) + 1)) : 0;
        if (hasGct && 13 + gctSize <= data.Length)
        { _colorTable = new byte[gctSize]; Array.Copy(data, 13, _colorTable, 0, gctSize); }
        var pos = 13 + gctSize;
        var delayMs = 100;
        while (pos < data.Length)
        {
            var block = data[pos];
            if (block == 0x3B)
            {
                break;
            }

            if (block == 0x21)
            { pos++; if (pos >= data.Length) { break; } var label = data[pos++]; if (label == 0xF9 && pos + 4 < data.Length) { var bs = data[pos++]; if (bs >= 4) { delayMs = (data[pos + 1] | (data[pos + 2] << 8)) * 10; if (delayMs < 20) { delayMs = 100; } pos += bs; } } while (pos < data.Length) { var sz = data[pos++]; if (sz == 0) { break; } pos += sz; } continue; }
            if (block == 0x2C)
            {
                if (_frames.Count >= MaxFrames)
                {
                    break;
                }

                if (pos + 10 > data.Length)
                {
                    break;
                }

                var imgW = data[pos + 5] | (data[pos + 6] << 8);
                var imgH = data[pos + 7] | (data[pos + 8] << 8);
                var imgPacked = data[pos + 9];
                var hasLct = (imgPacked & 0x80) != 0;
                var lctSize = hasLct ? 3 * (1 << ((imgPacked & 0x07) + 1)) : 0;
                pos += 10;
                if (hasLct && pos + lctSize <= data.Length)
                { var lct = new byte[lctSize]; Array.Copy(data, pos, lct, 0, lctSize); _colorTable = lct; }
                pos += lctSize;
                if (pos >= data.Length)
                {
                    break;
                }

                var lzwMin = data[pos++];
                var lzwData = new List<byte>();
                while (pos < data.Length)
                {
                    var sz = data[pos++];
                    if (sz == 0)
                    {
                        break;
                    }

                    if (pos + sz > data.Length)
                    {
                        break;
                    }

                    for (int i = 0; i < sz; i++)
                    {
                        lzwData.Add(data[pos++]);
                    }
                }
                var px = DecodeLzw(lzwData.ToArray(), lzwMin, imgW * imgH);
                _frames.Add(px);
                _frameSizes.Add((imgW, imgH));
                _delaysMs.Add(delayMs);
                continue;
            }
            pos++;
        }
    }

    private static byte[] DecodeLzw(byte[] input, int minCodeSize, int pixelCount)
    {
        var clearCode = 1 << minCodeSize;
        var endCode = clearCode + 1;
        var output = new byte[pixelCount];
        var outPos = 0;
        var codeSize = minCodeSize + 1;
        var nextCode = endCode + 1;
        var codeMask = (1 << codeSize) - 1;
        var table = new (int prefix, byte suffix, int length)[4096];
        for (int i = 0; i < clearCode; i++)
        {
            table[i] = (-1, (byte)i, 1);
        }

        var bitBuf = 0;
        var bitCount = 0;
        var inputPos = 0;
        var prevCode = -1;
        while (outPos < pixelCount && inputPos < input.Length)
        {
            while (bitCount < codeSize && inputPos < input.Length)
            { bitBuf |= input[inputPos++] << bitCount; bitCount += 8; }
            var code = bitBuf & codeMask;
            bitBuf >>= codeSize;
            bitCount -= codeSize;
            if (code == endCode)
            {
                break;
            }

            if (code == clearCode)
            { codeSize = minCodeSize + 1; nextCode = endCode + 1; codeMask = (1 << codeSize) - 1; prevCode = -1; continue; }
            int outputCode;
            if (code < nextCode)
            { outputCode = code; }
            else
            { if (prevCode < 0) { break; } outputCode = nextCode; table[nextCode] = (prevCode, GetFirstChar(table, prevCode), table[prevCode].length + 1); }
            var len = table[outputCode].length;
            if (outPos + len > pixelCount)
            {
                len = pixelCount - outPos;
            }

            var tempPos = outPos + len - 1;
            var c = outputCode;
            for (int i = 0; i < len && tempPos >= 0; i++)
            { if (c < 0 || c >= 4096) { break; } output[tempPos--] = table[c].suffix; c = table[c].prefix; }
            outPos += len;
            if (prevCode >= 0 && nextCode < 4096 && code != nextCode)
            { table[nextCode] = (prevCode, GetFirstChar(table, outputCode), table[prevCode].length + 1); nextCode++; if (nextCode > codeMask && codeSize < 12) { codeSize++; codeMask = (1 << codeSize) - 1; } }
            else if (code == nextCode)
            { nextCode++; if (nextCode > codeMask && codeSize < 12) { codeSize++; codeMask = (1 << codeSize) - 1; } }
            prevCode = code;
        }
        return output;
    }

    private static byte GetFirstChar((int prefix, byte suffix, int length)[] table, int code)
    { while (code >= 0 && code < 4096 && table[code].prefix >= 0) { code = table[code].prefix; } return code >= 0 && code < 4096 ? table[code].suffix : (byte)0; }
}
