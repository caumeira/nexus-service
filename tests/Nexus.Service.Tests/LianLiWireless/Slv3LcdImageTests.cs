using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdImageTests
{
    [Fact]
    public async Task EncodeAsync_produces_a_400x400_jpeg_under_the_size_cap()
    {
        if (FfmpegResolver.Path is null)
        {
            return;
        }

        var sourceBytes = await GenerateTestPngAsync();

        var result = await Slv3LcdImage.EncodeAsync(sourceBytes, ".png");

        Assert.True(result.Ok, result.Error);
        var jpeg = result.JpegBytes!;
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.True(jpeg.Length <= Slv3LcdImage.MaxJpegBytes, $"jpeg was {jpeg.Length} bytes");

        var (width, height) = ReadJpegSize(jpeg);
        Assert.Equal(400, width);
        Assert.Equal(400, height);
    }

    [Fact]
    public async Task EncodeAsync_fails_on_empty_input()
    {
        var result = await Slv3LcdImage.EncodeAsync(Array.Empty<byte>(), ".png");
        Assert.False(result.Ok);
    }

    private static async Task<byte[]> GenerateTestPngAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slv3-lcd-test-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegResolver.Path!,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "color=red:size=800x600", "-frames:v", "1", path })
            {
                psi.ArgumentList.Add(arg);
            }
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    // Scans JPEG markers for the first SOFn segment to read width/height
    // directly, without a new image-parsing dependency.
    private static (int Width, int Height) ReadJpegSize(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                pos++;
                continue;
            }
            var marker = jpeg[pos + 1];
            pos += 2;
            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }
            if (pos + 1 >= jpeg.Length)
            {
                break;
            }
            var segmentLength = (jpeg[pos] << 8) | jpeg[pos + 1];
            var isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                var height = (jpeg[pos + 3] << 8) | jpeg[pos + 4];
                var width = (jpeg[pos + 5] << 8) | jpeg[pos + 6];
                return (width, height);
            }
            pos += segmentLength;
        }
        throw new InvalidOperationException("No SOF marker found in JPEG.");
    }
}
