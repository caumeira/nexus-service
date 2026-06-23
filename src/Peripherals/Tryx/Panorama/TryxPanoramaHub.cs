using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Normalized (0..1) crop rectangle the dashboard cropper produced, applied
/// in the ffmpeg transcode so the user's framing fills the panel without letterboxing.</summary>
public readonly record struct TryxVideoCrop(double X, double Y, double W, double H);

/// <summary>
/// Singleton coordinator for a Tryx Panorama AIO device. Owns the open transport,
/// the shared <see cref="TryxPanoramaState"/> snapshot, and the high-level
/// operations the heartbeat worker and REST routes call into.
/// Hot-plug is self-healing: each <see cref="EnsureConnected"/> re-runs port discovery.
/// </summary>
public sealed class TryxPanoramaHub : IDisposable
{
    private readonly ITryxPanoramaPanelDiscovery _discovery;
    private readonly Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> _transportFactory;
    private readonly object _lock = new();
    private ITryxPanoramaTransport? _transport;
    private bool _disposed;
    // Paused during an import so the heartbeat's STATE-all frames don't interleave
    // the transport->transported handshake on the shared serial port.
    private volatile bool _importInProgress;

    public TryxPanoramaHub(
        ITryxPanoramaPanelDiscovery discovery,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> transportFactory)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
    }

    public TryxPanoramaState State { get; } = new();

    /// <summary>True while an import holds the port; the heartbeat skips STATE-all then.</summary>
    public bool ImportInProgress => _importInProgress;

    public bool IsConnected => _transport is { IsOpen: true };

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            foreach (var port in _discovery.Discover())
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    State.AdbSerial = port.AdbSerial;
                    State.PortName = port.PortName;
                    State.ProductId = port.ProductId;
                    State.ModelName = TryxPanoramaProtocol.GetModelName(port.ProductId);
                    State.LastConnectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ServiceLog.Info($"[tryx] connected to {port.PortName} (serial={port.Serial}, adb={port.AdbSerial})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tryx] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
    }

    public bool SendConn()
        => SendOnly(TryxPanoramaProtocol.BuildConn());

    public bool SendStateAll()
        => SendOnly(TryxPanoramaProtocol.BuildStateAll());

    public bool SetEnabled(bool enable)
    {
        var ok = SendOnly(TryxPanoramaProtocol.BuildWaterBlockScreen(enable));
        if (ok) State.ScreenEnabled = enable;
        return ok;
    }

    public bool SetBrightness(int brightness)
    {
        var clamped = Math.Clamp(brightness, 0, 100);
        EnsureSelection();
        var frame = State.CurrentMediaIsCustom
            ? TryxPanoramaProtocol.BuildConfigCustom(clamped, State.CurrentMedia)
            : TryxPanoramaProtocol.BuildConfigPreset(clamped, State.CurrentMedia);
        var ok = SendOnly(frame);
        if (ok) State.Brightness = clamped;
        return ok;
    }

    // Brightness/fan ride the full config, which the device ignores without an id
    // block. With nothing chosen yet (e.g. right after a service restart, before
    // any selection), default to the first preset so the control still applies.
    private const string DefaultSelectionId = "Pre-set 1: Cooling delivery";

    private void EnsureSelection()
    {
        if (string.IsNullOrEmpty(State.CurrentMedia))
        {
            State.CurrentMedia = DefaultSelectionId;
            State.CurrentMediaIsCustom = false;
        }
    }

    public bool SetPreset(string presetId)
    {
        var ok = SendOnly(TryxPanoramaProtocol.BuildConfigPreset(State.Brightness, presetId));
        if (ok)
        {
            State.CurrentMedia = presetId;
            State.CurrentMediaIsCustom = false;
        }
        return ok;
    }

    public bool SetFanSmart(int[][]? curve)
    {
        EnsureSelection();
        var frame = State.CurrentMediaIsCustom
            ? TryxPanoramaProtocol.BuildConfigCustom(State.Brightness, State.CurrentMedia, curve)
            : TryxPanoramaProtocol.BuildConfigPreset(State.Brightness, State.CurrentMedia, curve);
        return SendOnly(frame);
    }

    public bool SetFanFixed(int percent)
    {
        EnsureSelection();
        return SendOnly(TryxPanoramaProtocol.BuildConfigFanFixed(
            State.Brightness, State.CurrentMedia, State.CurrentMediaIsCustom, Math.Clamp(percent, 0, 100)));
    }

    public async Task<bool> ImportAndPlayVideoAsync(
        string localPath, string sourceName, TryxVideoCrop? crop, int targetWidth, int targetHeight, CancellationToken ct)
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            Console.Error.WriteLine("[tryx] adb not found; cannot push video");
            return false;
        }

        var adbSerial = State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            adbSerial = RescanAdbSerial(adbPath);
            if (string.IsNullOrEmpty(adbSerial))
            {
                Console.Error.WriteLine("[tryx] no ADB serial found for Panorama device");
                return false;
            }
            State.AdbSerial = adbSerial;
        }

        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            Console.Error.WriteLine("[tryx] ffmpeg not found; cannot transcode");
            return false;
        }

        // Name the on-device file after the user's source, not the temp upload path.
        var deviceFileName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourceName)) + ".mp4";
        var tempOutput = Path.Combine(Path.GetTempPath(), $"nexus-tryx-out-{Guid.NewGuid()}.mp4");

        _importInProgress = true;
        try
        {
            // Transcode to device-compatible format.
            if (!await TranscodeAsync(ffmpegPath, localPath, tempOutput, crop, targetWidth, targetHeight, ct))
            {
                Console.Error.WriteLine("[tryx] transcode failed");
                return false;
            }

            var fileInfo = new FileInfo(tempOutput);
            if (!fileInfo.Exists)
            {
                Console.Error.WriteLine("[tryx] transcoded file not found");
                return false;
            }
            var fileSize = fileInfo.Length;
            var localMd5 = ComputeMd5(tempOutput);

            // Send transport frame.
            if (!SendOnly(TryxPanoramaProtocol.BuildTransport(fileSize, deviceFileName)))
            {
                Console.Error.WriteLine("[tryx] transport frame failed");
                return false;
            }

            // Push + MD5 verify, up to 4 attempts.
            var pushed = false;
            for (var attempt = 1; attempt <= 4 && !pushed; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                RunAdb(adbPath, $"-s {adbSerial} push \"{tempOutput}\" /sdcard/pcMedia/{deviceFileName}",
                    60_000, out var pushOut, out var pushErr);

                RunAdb(adbPath, $"-s {adbSerial} shell md5sum /sdcard/pcMedia/{deviceFileName}",
                    10_000, out var md5Out, out _);

                var deviceMd5 = md5Out.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                    ? parts[0]
                    : "";

                if (string.Equals(localMd5, deviceMd5, StringComparison.OrdinalIgnoreCase))
                {
                    pushed = true;
                }
                else
                {
                    Console.Error.WriteLine($"[tryx] md5 mismatch on attempt {attempt}: local={localMd5} device={deviceMd5}");
                }
            }

            if (!pushed)
            {
                Console.Error.WriteLine("[tryx] push failed after 4 attempts");
                return false;
            }

            // gap matching nx_e2e.ps1 send cadence (push -> transported)
            await Task.Delay(200, ct);

            if (!SendOnly(TryxPanoramaProtocol.BuildTransported(localMd5, deviceFileName)))
            {
                Console.Error.WriteLine("[tryx] transported frame failed");
                return false;
            }

            // gap matching nx_e2e.ps1 send cadence (transported -> waterBlockScreen)
            await Task.Delay(400, ct);

            if (!SendOnly(TryxPanoramaProtocol.BuildWaterBlockScreen(true)))
            {
                Console.Error.WriteLine("[tryx] waterBlockScreen frame failed");
                return false;
            }

            // gap matching nx_e2e.ps1 send cadence (waterBlockScreen -> config)
            await Task.Delay(400, ct);

            if (!SendOnly(TryxPanoramaProtocol.BuildConfigCustom(State.Brightness, deviceFileName)))
            {
                Console.Error.WriteLine("[tryx] config frame failed");
                return false;
            }

            // settle gap after config before reporting success (nx_e2e.ps1 cadence)
            await Task.Delay(800, ct);

            State.CurrentMedia = deviceFileName;
            State.CurrentMediaIsCustom = true;
            State.ScreenEnabled = true;
            return true;
        }
        finally
        {
            _importInProgress = false;
            try { File.Delete(tempOutput); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    private bool SendOnly(byte[] request)
    {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tryx] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    private static async Task<bool> TranscodeAsync(
        string ffmpegPath, string input, string output, TryxVideoCrop? crop,
        int targetWidth, int targetHeight, CancellationToken ct)
    {
        var tw = Math.Clamp(targetWidth, 16, 4096);
        var th = Math.Clamp(targetHeight, 16, 4096);
        string vf;
        if (crop is { } c && c.W > 0.001 && c.H > 0.001)
        {
            // Apply the dashboard cropper's normalized rectangle, then scale to the
            // target; the crop already matches the target aspect so no pad is needed.
            // Invariant culture: ffmpeg expects '.' decimals regardless of host locale.
            // No setsar: the bundled ffmpeg omits that filter, and the panel renders the
            // decoded pixel grid to its screen regardless of the sample aspect ratio.
            string x = Clamp01(c.X).ToString("0.######", CultureInfo.InvariantCulture);
            string y = Clamp01(c.Y).ToString("0.######", CultureInfo.InvariantCulture);
            string w = Clamp01(c.W).ToString("0.######", CultureInfo.InvariantCulture);
            string h = Clamp01(c.H).ToString("0.######", CultureInfo.InvariantCulture);
            vf = $"crop=iw*{w}:ih*{h}:iw*{x}:ih*{y},scale={tw}:{th}";
        }
        else
        {
            // No crop: preserve aspect and letterbox-pad to the target.
            vf = $"scale={tw}:{th}:force_original_aspect_ratio=decrease,pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2";
        }
        // Encode params from the validated nx_e2e.ps1 recipe: the panel's HW H.264
        // decoder needs High@L3.2, yuv420p, NO B-frames, ref=1, timescale 90000.
        var args =
            "-nostdin -hide_banner -loglevel error -nostats " +
            $"-i \"{input}\" " +
            $"-vf \"{vf}\" " +
            "-pix_fmt yuv420p -an -c:v libx264 -profile:v high -level 3.2 " +
            "-x264-params \"ref=1:bframes=0:slices=4:sliced-threads=1:keyint=12:keyint_min=1:me=dia:subme=1:trellis=0:weightp=1:mbtree=0:8x8dct=1:cabac=1:deblock=1,0,0:analyse=0x3,0x3\" " +
            "-video_track_timescale 90000 " +
            $"-y \"{output}\"";

        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        p.Start();
        // Drain stdout+stderr: a redirected stream that is never read deadlocks the
        // child once the OS pipe buffer fills (ffmpeg writes enough to hang there).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
            var err = await stderrTask;
            await stdoutTask;
            if (p.ExitCode != 0)
            {
                var tail = err.Length > 400 ? err.Substring(err.Length - 400) : err;
                ServiceLog.Error($"[tryx] ffmpeg exit {p.ExitCode}: {tail.Replace('\r', ' ').Replace('\n', ' ')}");
            }
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* best effort */ }
            throw;
        }
    }

    private static void RunAdb(string adbPath, string arguments, int timeoutMs, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                stderr = "timed out";
                return;
            }
            stdout = p.StandardOutput.ReadToEnd();
            stderr = p.StandardError.ReadToEnd();
        }
        catch (Exception ex)
        {
            stderr = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string RescanAdbSerial(string adbPath)
    {
        RunAdb(adbPath, "devices -l", 5_000, out var output, out _);
        foreach (var line in output.Split('\n'))
        {
            if (line.IndexOf("product:cm01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("model:cm01", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) return parts[0];
            }
        }
        return "";
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static string ComputeMd5(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        var result = sb.ToString();
        if (result.Length > 60) result = result.Substring(0, 60);
        if (string.IsNullOrEmpty(result)) result = "media";
        return result;
    }
}
