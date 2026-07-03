using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

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
    private readonly ISensorProvider _sensors;
    private readonly IConfigStore _configStore;
    private readonly object _lock = new();
    private ITryxPanoramaTransport? _transport;
    private bool _disposed;
    // Set while EnsureLocalCopyAsync holds the adb pull, so the heartbeat does not
    // issue a concurrent adb command against the same device.
    private volatile bool _importInProgress;
    private readonly TryxOverlayConfig _overlay;

    public TryxPanoramaHub(
        ITryxPanoramaPanelDiscovery discovery,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> transportFactory,
        ISensorProvider sensors,
        IConfigStore configStore)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _sensors = sensors;
        _configStore = configStore;

        var saved = configStore.Load().Tryx;
        _overlay = new TryxOverlayConfig
        {
            Stats = saved.OverlayStats,
            Color = saved.OverlayColor,
            Align = saved.OverlayAlign,
            Filter = saved.OverlayFilter,
            Opacity = saved.OverlayOpacity,
            PosX = saved.OverlayPosX,
            PosY = saved.OverlayPosY,
            Font = saved.OverlayFont,
            Size = saved.OverlaySize,
        };
        State.CurrentMedia = saved.CurrentMedia;
        State.CurrentMediaIsCustom = saved.CurrentMediaIsCustom;
        State.Brightness = saved.Brightness;
    }

    public TryxPanoramaState State { get; } = new();

    /// <summary>True while an import holds the port; the heartbeat skips STATE-all then.</summary>
    public bool ImportInProgress => _importInProgress;

    public bool IsConnected => _transport is { IsOpen: true };

    public TryxOverlayConfig Overlay => _overlay;

    /// <summary>Wallpaper preset ids the connected panel reported as on-device
    /// (see <see cref="ITryxPanoramaTransport.AvailableMediaIds"/>); empty before
    /// the panel's media-list push arrives or when disconnected.</summary>
    public IReadOnlyList<string> AvailableMediaIds => _transport?.AvailableMediaIds ?? Array.Empty<string>();

    /// <summary>Filenames the panel reported it has stored (device truth), or empty if it
    /// has not pushed its list this session (it does so only on a cold boot / re-enumeration).</summary>
    public IReadOnlyList<string> AvailableMediaFilenames => _transport?.AvailableMediaFilenames ?? Array.Empty<string>();

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
                    ApplyInitialConfig(t);
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

    // Sends the persisted brightness on fresh connect so the panel picks it up
    // without requiring a dashboard interaction. Called only from inside the
    // EnsureConnected lock after _transport is set; uses the transport reference
    // directly to avoid re-entering EnsureConnected.
    private void ApplyInitialConfig(ITryxPanoramaTransport transport)
    {
        try
        {
            transport.Write(TryxRkProtocol.BuildConfig(State.ScreenEnabled, State.Brightness));
            if (_overlay.Stats.Length > 0)
            {
                transport.Write(TryxRkProtocol.BuildOverlay(
                    BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(_overlay.Color),
                    _overlay.Font, _overlay.Size));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tryx] initial config apply failed: {ex.GetType().Name}: {ex.Message}");
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
        => SendOnly(TryxRkProtocol.BuildHeartbeat());

    /// <summary>One heartbeat tick. Skips entirely if a file transfer holds the send gate,
    /// so no control frame lands between the transfer's BEGIN/DATA/COMMIT frames.</summary>
    public void SendHeartbeatTick()
    {
        if (!Monitor.TryEnter(_txGate)) return;
        try
        {
            SendConn();
            if (!_importInProgress) SendStateAll();
            State.LastFrameMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        finally
        {
            Monitor.Exit(_txGate);
        }
    }

    public bool SendStateAll()
    {
        if (_overlay.Stats.Length == 0)
        {
            return true;
        }
        return SendOnly(TryxRkProtocol.BuildOverlay(
            BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(_overlay.Color),
            _overlay.Font, _overlay.Size));
    }

    public bool SetEnabled(bool enable)
    {
        // Screen on/off rides the same f200.f5 config as brightness (f5.f1 = enable);
        // carry the current brightness so turning the screen back on restores it.
        var ok = SendReliable(TryxRkProtocol.BuildConfig(enable, State.Brightness));
        if (ok) State.ScreenEnabled = enable;
        return ok;
    }

    public bool SetBrightness(int brightness)
    {
        var clamped = Math.Clamp(brightness, 0, 100);
        var ok = SendReliable(TryxRkProtocol.BuildConfig(State.ScreenEnabled, clamped));
        if (ok)
        {
            State.Brightness = clamped;
            _configStore.Update(s => s.Tryx.Brightness = clamped);
        }
        return ok;
    }

    /// <summary><paramref name="wallpaperMedia"/> is the panel's built-in wallpaper
    /// filename (TryxRkProtocol.PresetMediaFile). Screen state and brightness ride
    /// along so the panel keeps them.</summary>
    public bool SetPreset(string wallpaperMedia)
    {
        var ok = SendReliable(TryxRkProtocol.BuildPreset(wallpaperMedia, State.ScreenEnabled, State.Brightness));
        if (ok)
        {
            State.CurrentMedia = wallpaperMedia;
            State.CurrentMediaIsCustom = false;
            _configStore.Update(s =>
            {
                s.Tryx.CurrentMedia = wallpaperMedia;
                s.Tryx.CurrentMediaIsCustom = false;
            });
        }
        return ok;
    }

    public bool SetFanSmart(int[][]? curve)
    {
        ServiceLog.Warn("[tryx] SetFanSmart not yet implemented for RK firmware");
        return false;
    }

    public bool SetFanFixed(int percent)
    {
        ServiceLog.Warn("[tryx] SetFanFixed not yet implemented for RK firmware");
        return false;
    }

    public bool SetOverlay(TryxOverlayConfig overlay)
    {
        _overlay.Stats = overlay.Stats;
        _overlay.Color = overlay.Color;
        _overlay.Align = overlay.Align;
        _overlay.Filter = overlay.Filter;
        _overlay.Opacity = overlay.Opacity;
        _overlay.PosX = overlay.PosX;
        _overlay.PosY = overlay.PosY;
        _overlay.Font = overlay.Font;
        _overlay.Size = overlay.Size;
        _configStore.Update(s =>
        {
            s.Tryx.OverlayStats = overlay.Stats;
            s.Tryx.OverlayColor = overlay.Color;
            s.Tryx.OverlayAlign = overlay.Align;
            s.Tryx.OverlayFilter = overlay.Filter;
            s.Tryx.OverlayOpacity = overlay.Opacity;
            s.Tryx.OverlayPosX = overlay.PosX;
            s.Tryx.OverlayPosY = overlay.PosY;
            s.Tryx.OverlayFont = overlay.Font;
            s.Tryx.OverlaySize = overlay.Size;
        });
        return SendReliable(TryxRkProtocol.BuildOverlay(
            BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(overlay.Color),
            overlay.Font, overlay.Size));
    }

    // Panel surface is a fixed 2240x1080 2:1 display; every custom clip is transcoded to
    // it. Main profile + no B-frames matches Kanali's MediaX output (the format the
    // firmware decoder is known to accept).
    private const int PanelWidth = 2240;
    private const int PanelHeight = 1080;
    private const int PanelFps = 60;
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(5);

    private int _transferSession = 600000;
    // Held for the whole BEGIN..COMMIT burst; the heartbeat acquires the same gate so
    // no control frame lands mid-transfer (a per-frame transport lock is not enough).
    private readonly object _txGate = new();
    // Serializes imports so two concurrent uploads can't interleave transfers or race
    // the transfer flag on the single shared transport.
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly TryxCloudCatalog _cloud = new();

    /// <summary>Transcodes <paramref name="localPath"/> to the panel's H.264 format, wraps it
    /// in the Tryx media container, streams it over the RK file-transfer protocol, and selects
    /// it. RK custom media is a directly playable "media" file (no encryption).</summary>
    public async Task<bool> ImportAndPlayVideoAsync(
        string localPath, string sourceName, TryxVideoCrop? crop, int targetWidth, int targetHeight, CancellationToken ct)
    {
        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            ServiceLog.Error("[tryx] ffmpeg not found; cannot import video");
            return false;
        }
        if (!EnsureConnected()) return false;

        var deviceFileName = CustomMediaFileName(sourceName);
        var mp4Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-tryx-{Guid.NewGuid():N}.mp4");
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TranscodeToPanelMp4Async(ffmpegPath, localPath, crop, mp4Path, ct))
            {
                return false;
            }
            // Bundled ffmpeg has only the mp4 muxer; extract the Annex-B stream ourselves.
            var mp4 = await File.ReadAllBytesAsync(mp4Path, ct);
            byte[] h264;
            try { h264 = Mp4AnnexB.Convert(mp4); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[tryx] MP4->Annex-B conversion failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            var frames = CountH264Frames(h264);
            // Stable per-name id (GetHashCode is per-process randomized, which would give
            // the same file a different container id across restarts).
            var id = StableMediaId(deviceFileName);
            var container = TryxRkProtocol.WrapMediaContainer(h264, PanelFps, PanelWidth, PanelHeight, frames, id);

            if (!SendFileTransfer(deviceFileName, container, fileType: "media", ct))
            {
                return false;
            }
            // Same f200 config that selects a built-in preset; wallpaper = the pushed file.
            if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, State.ScreenEnabled, State.Brightness)))
            {
                return false;
            }
            State.CurrentMedia = deviceFileName;
            State.CurrentMediaIsCustom = true;
            State.ScreenEnabled = true;
            _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = true; });

            // Thumbnail/duration from the source so the media list can render the entry.
            try { TryxThumbnailCache.Write(ffmpegPath, localPath, deviceFileName); } catch { }
            try { TryxThumbnailCache.WriteDuration(deviceFileName, TryxThumbnailCache.ProbeDuration(ffmpegPath, localPath)); } catch { }
            return true;
        }
        finally
        {
            try { File.Delete(mp4Path); } catch { /* best effort */ }
            _importGate.Release();
        }
    }

    /// <summary>Downloads a cloud wallpaper, decrypts it, and installs it. The decrypted asset
    /// is the plain Tryx media container (validated by its magic before pushing, since the SM4
    /// mode is inferred). Returns (ok, message).</summary>
    public async Task<(bool Ok, string Msg)> InstallCloudMaterialAsync(int id, CancellationToken ct)
    {
        if (!EnsureConnected()) return (false, "Tryx Panorama not connected");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-tryx-cloud-{id}-{Guid.NewGuid():N}.bin");
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string installName;
            try { installName = await _cloud.DownloadAndDecryptAsync(id, tmp, ct); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[tryx] cloud download/decrypt failed for {id}: {ex.GetType().Name}: {ex.Message}");
                return (false, "download failed");
            }
            // A correct SM4-CBC decrypt yields a raw H.264 Annex-B stream (start code
            // 00 00 00 01 then an SPS); a wrong key/mode gives garbage, so gate the push on it.
            var head = new byte[16];
            int n;
            await using (var fs = File.OpenRead(tmp))
            {
                n = await fs.ReadAsync(head, ct);
            }
            if (n < 5 || head[0] != 0x00 || head[1] != 0x00 || head[2] != 0x00 || head[3] != 0x01)
            {
                ServiceLog.Error($"[tryx] cloud {id} decrypt not H.264 (head={Convert.ToHexString(head[..Math.Min(n, 16)])})");
                return (false, "decrypt format error");
            }
            if (!await InstallLocalMediaAsync(tmp, installName, ct)) return (false, "install failed");
            _configStore.Update(s =>
            {
                if (!s.Tryx.InstalledCloudIds.Contains(id)) s.Tryx.InstalledCloudIds.Add(id);
            });
            return (true, "");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            _importGate.Release();
        }
    }

    public Task<List<TryxCloudMaterial>> GetCloudCatalogAsync(CancellationToken ct)
        => _cloud.GetCatalogAsync("PANO_1011", ct);

    /// <summary>Whether cloud material <paramref name="id"/> is on the panel. The panel is the
    /// source of truth when it has reported its media list (a "download_{id}." file); otherwise
    /// falls back to our persisted record, so a fresh install self-heals once the panel lists it.</summary>
    public bool IsCloudInstalled(int id)
    {
        var prefix = $"download_{id}.";
        foreach (var name in AvailableMediaFilenames)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return _configStore.Load().Tryx.InstalledCloudIds.Contains(id);
    }

    public Task<System.IO.Stream?> OpenCloudCoverAsync(int id, CancellationToken ct)
        => _cloud.OpenCoverAsync(id, ct);

    /// <summary>Installs an already-local, panel-ready file by streaming it over the file-transfer
    /// protocol and selecting it. The file must already be the plain Tryx media container;
    /// <paramref name="deviceFileName"/> is the on-panel name. Caller holds the import gate.</summary>
    public async Task<bool> InstallLocalMediaAsync(string localContainerPath, string deviceFileName, CancellationToken ct)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return false;
        if (!EnsureConnected()) return false;
        var bytes = await File.ReadAllBytesAsync(localContainerPath, ct);
        if (!SendFileTransfer(deviceFileName, bytes, fileType: "media", ct)) return false;
        if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, State.ScreenEnabled, State.Brightness))) return false;
        State.CurrentMedia = deviceFileName;
        State.CurrentMediaIsCustom = false;
        _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = false; });
        return true;
    }

    // FNV-1a over the name: a process-stable 32-bit id for the media container header.
    private static uint StableMediaId(string name)
    {
        uint h = 2166136261;
        foreach (var c in name) { h = (h ^ c) * 16777619; }
        return h | 1u;
    }

    // Device filename for a custom upload: sanitized stem + the panel's media suffix, matching
    // the built-in default_NN.mp4.h264_2240x1080 naming so the f200 select targets it.
    private static string CustomMediaFileName(string sourceName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(sourceName);
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            sb.Append((c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-') ? c : '_');
        }
        var clean = sb.ToString();
        if (clean.Length > 32) clean = clean.Substring(0, 32);
        if (clean.Length == 0) clean = "custom";
        return $"{clean}.mp4.h264_{PanelWidth}x{PanelHeight}";
    }

    // Streams a full file: BEGIN (f400 name+size) -> DATA (f401 chunks) -> COMMIT (f402 type).
    // Holds _txGate so the heartbeat skips its whole tick for the burst (Kanali sends none
    // mid-transfer; the receiving panel will not standby). Any failure or cancellation drops
    // the transport, because a partial transfer leaves the panel awaiting more chunks - a
    // re-enumeration resyncs it, and a stray control frame into a half-open session is worse
    // than a reconnect.
    private bool SendFileTransfer(string deviceFileName, byte[] payload, string fileType, CancellationToken ct)
    {
        var session = unchecked((uint)Interlocked.Increment(ref _transferSession));
        lock (_txGate)
        {
            var completed = false;
            try
            {
                if (!SendOnly(TryxRkProtocol.BuildFileBegin(session, deviceFileName, payload.Length))) return false;
                for (var offset = 0; offset < payload.Length; offset += TryxRkProtocol.FileChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var len = Math.Min(TryxRkProtocol.FileChunkSize, payload.Length - offset);
                    if (!SendOnly(TryxRkProtocol.BuildFileChunk(session, payload.AsSpan(offset, len)))) return false;
                }
                completed = SendOnly(TryxRkProtocol.BuildFileCommit(session, fileType));
                return completed;
            }
            finally
            {
                if (!completed) Disconnect();
            }
        }
    }

    private async Task<bool> TranscodeToPanelMp4Async(
        string ffmpegPath, string input, TryxVideoCrop? crop, string outputMp4, CancellationToken ct)
    {
        var vf = crop is { } c
            ? $"crop=in_w*{F(c.W)}:in_h*{F(c.H)}:in_w*{F(c.X)}:in_h*{F(c.Y)},scale={PanelWidth}:{PanelHeight}"
            : $"scale={PanelWidth}:{PanelHeight}";
        var args = $"-nostdin -hide_banner -loglevel error -y -i \"{input}\" -an " +
                   $"-vf \"{vf}\" -c:v libx264 -profile:v main -pix_fmt yuv420p " +
                   $"-r {PanelFps} -bf 0 -g {PanelFps} -movflags +faststart \"{outputMp4}\"";
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        if (p is null) return false;
        // Bound the run and kill the child on cancel/timeout so it can't orphan (leaving
        // the output file locked); WaitForExitAsync/ReadToEnd only observe, they don't kill.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TranscodeTimeout);
        try
        {
            var err = await p.StandardError.ReadToEndAsync(timeoutCts.Token);
            await p.WaitForExitAsync(timeoutCts.Token);
            if (p.ExitCode != 0 || !File.Exists(outputMp4) || new FileInfo(outputMp4).Length == 0)
            {
                ServiceLog.Error($"[tryx] transcode failed (exit {p.ExitCode}): {err.Trim()}");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ServiceLog.Error("[tryx] transcode cancelled or timed out");
            return false;
        }
    }

    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    // Counts H.264 access units by VCL NAL units (type 1 non-IDR, 5 IDR). x264 emits one
    // slice per frame by default, so this equals the frame count; used for the container's
    // frame-count header field.
    private static int CountH264Frames(ReadOnlySpan<byte> h264)
    {
        var frames = 0;
        for (var i = 0; i + 3 < h264.Length; i++)
        {
            if (h264[i] == 0 && h264[i + 1] == 0 && h264[i + 2] == 1)
            {
                var nalType = h264[i + 3] & 0x1f;
                if (nalType is 1 or 5) frames++;
            }
        }
        return frames;
    }

    /// <summary>Ensures a local copy of <paramref name="deviceFileName"/> exists in the media store,
    /// pulling from the device via adb if necessary. Holds the import-pause for the duration
    /// of the pull so the heartbeat does not issue concurrent adb commands.</summary>
    public Task<bool> EnsureLocalCopyAsync(string deviceFileName, CancellationToken ct)
    {
        if (TryxMediaStore.Exists(deviceFileName))
        {
            return Task.FromResult(true);
        }

        var adbSerial = State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            return Task.FromResult(false);
        }

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return Task.FromResult(false);
        }

        ct.ThrowIfCancellationRequested();

        var destPath = TryxMediaStore.Path(deviceFileName);
        _importInProgress = true;
        try
        {
            Directory.CreateDirectory(TryxMediaStore.StoreDir);
            RunAdb(adbPath, $"-s {adbSerial} pull /sdcard/pcMedia/{deviceFileName} \"{destPath}\"",
                60_000, out _, out _);

            if (!File.Exists(destPath))
            {
                return Task.FromResult(false);
            }

            var ffmpegPath = FfmpegResolver.Path;
            if (ffmpegPath is not null && !File.Exists(TryxThumbnailCache.ThumbPath(deviceFileName)))
            {
                try { TryxThumbnailCache.Write(ffmpegPath, destPath, deviceFileName); } catch { }
                try
                {
                    var dur = TryxThumbnailCache.ProbeDuration(ffmpegPath, destPath);
                    TryxThumbnailCache.WriteDuration(deviceFileName, dur);
                }
                catch { }
            }

            return Task.FromResult(true);
        }
        finally
        {
            _importInProgress = false;
        }
    }

    /// <summary>Selects an already-on-device custom file without re-pushing it. The RK
    /// firmware selects any on-panel media (preset or custom) with the same f200 config.</summary>
    public bool SelectCustomMedia(string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return false;
        if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, screenOn: true, State.Brightness))) return false;
        State.CurrentMedia = deviceFileName;
        State.CurrentMediaIsCustom = true;
        State.ScreenEnabled = true;
        _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = true; });
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    /// <summary>
    /// Builds the STATE-all sensor JSON from live ISensorProvider reads.
    /// Sensor name patterns match LHM's naming conventions (Windows path);
    /// Mac/Linux providers return subsets - missing sensors produce 0.
    /// </summary>
    internal string BuildLiveSensorJsonForTest() => BuildLiveSensorJson();

    /// <summary>Sensor values read for the STATE-all JSON and the overlay stat mapper,
    /// so both consume the same derivation instead of duplicating sensor lookups.</summary>
    private readonly struct TryxSensorReadout
    {
        public int CpuTemp { get; init; }
        public int CpuLoad { get; init; }
        public int CpuClock { get; init; }
        public int CpuPower { get; init; }
        public int CpuVoltage { get; init; }
        public int GpuTemp { get; init; }
        public int GpuLoad { get; init; }
        public int GpuClock { get; init; }
        public int GpuPower { get; init; }
        public int GpuVoltage { get; init; }
        public int MemTotal { get; init; }
        public int MemUsed { get; init; }
        public int MemLoad { get; init; }
        public int MemClock { get; init; }
        public int MoboTemp { get; init; }
        public int PchTemp { get; init; }
    }

    private TryxSensorReadout ReadSensors()
    {
        var cpuSensors = _sensors.GetCpuSensors();
        var gpuSensors = GetPrimaryGpuSensors();
        var memSensors = _sensors.GetMemorySensors();
        var moboSensors = _sensors.GetMotherboardSensors();

        var cpuTemp = RoundSensor(cpuSensors, "Temperature", "Package");
        var cpuLoad = RoundSensor(cpuSensors, "Load", "CPU Total");
        var cpuClock = RoundSensor(cpuSensors, "Clock", "Core Max");
        if (cpuClock == 0) cpuClock = MaxClock(cpuSensors, "Core");
        var cpuPower = RoundSensor(cpuSensors, "Power", "Package");
        var cpuVoltage = RoundSensor(cpuSensors, "Voltage", "VCore");

        // "Core" matches "GPU Core" on NVIDIA/AMD; fall back to first Temperature
        // sensor for GPUs that name the die temperature differently.
        var gpuTemp = (int)Math.Round(
            (FindSensor(gpuSensors, "Temperature", "Core")
             ?? FindSensor(gpuSensors, "Temperature", null))?.Value ?? 0f);
        var gpuLoad = RoundSensor(gpuSensors, "Load", "Core");
        var gpuClock = RoundSensor(gpuSensors, "Clock", "Core");
        var gpuPower = RoundSensor(gpuSensors, "Power", null);
        var gpuVoltage = (int)Math.Round(
            (FindSensor(gpuSensors, "Voltage", "Core")
             ?? FindSensor(gpuSensors, "Voltage", null))?.Value ?? 0f);

        // Memory Used/Available are Data type in GB; convert to integer GB.
        var memUsed = (int)Math.Round(FindSensor(memSensors, "Data", "Used")?.Value ?? 0f);
        var memAvail = (int)Math.Round(FindSensor(memSensors, "Data", "Available")?.Value ?? 0f);
        var memTotal = memUsed + memAvail;
        var memLoad = RoundSensor(memSensors, "Load", null);
        // AMD exposes RAM clock as "Memory" under CPU Clock sensors; Intel/other
        // may expose it under motherboard sensors. Fall back to 0 when unavailable.
        var memClock = (int)Math.Round(
            (FindSensor(cpuSensors, "Clock", "Memory")
             ?? FindSensor(moboSensors, "Clock", "Memory"))?.Value ?? 0f);

        var moboTemp = RoundSensor(moboSensors, "Temperature", null);
        var pchTemp = RoundSensor(moboSensors, "Temperature", "PCH");

        return new TryxSensorReadout
        {
            CpuTemp = cpuTemp,
            CpuLoad = cpuLoad,
            CpuClock = cpuClock,
            CpuPower = cpuPower,
            CpuVoltage = cpuVoltage,
            GpuTemp = gpuTemp,
            GpuLoad = gpuLoad,
            GpuClock = gpuClock,
            GpuPower = gpuPower,
            GpuVoltage = gpuVoltage,
            MemTotal = memTotal,
            MemUsed = memUsed,
            MemLoad = memLoad,
            MemClock = memClock,
            MoboTemp = moboTemp,
            PchTemp = pchTemp,
        };
    }

    private string BuildLiveSensorJson()
    {
        var r = ReadSensors();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sb = new StringBuilder(512);
        sb.Append("{\"network\":{\"upload\":0,\"download\":0},");
        sb.Append("\"memory\":{\"total\":").Append(r.MemTotal)
          .Append(",\"used\":").Append(r.MemUsed)
          .Append(",\"load\":").Append(r.MemLoad)
          .Append(",\"temperature\":0,\"speed\":").Append(r.MemClock).Append("},");
        sb.Append("\"cpu\":{\"load\":").Append(r.CpuLoad)
          .Append(",\"usage\":").Append(r.CpuLoad)
          .Append(",\"temperature\":").Append(r.CpuTemp)
          .Append(",\"speedAverage\":").Append(r.CpuClock)
          .Append(",\"power\":").Append(r.CpuPower)
          .Append(",\"voltage\":").Append(r.CpuVoltage).Append("},");
        sb.Append("\"gpu\":{\"load\":").Append(r.GpuLoad)
          .Append(",\"temperature\":").Append(r.GpuTemp)
          .Append(",\"fan\":0,\"speed\":").Append(r.GpuClock)
          .Append(",\"power\":").Append(r.GpuPower)
          .Append(",\"voltage\":").Append(r.GpuVoltage).Append("},");
        sb.Append("\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0},");
        sb.Append("\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}],");
        sb.Append("\"motherboard\":{\"temperature\":").Append(r.MoboTemp)
          .Append(",\"pchTemperature\":").Append(r.PchTemp).Append("},");
        sb.Append("\"timestamp\":").Append(ts).Append('}');
        return sb.ToString();
    }

    /// <summary>Maps a dashboard stat label to its overlay value string; unmapped
    /// labels are skipped. "Date&amp;Time" reads the local clock, not a sensor.</summary>
    private static TryxOverlayLine? MapOverlayStat(string stat, in TryxSensorReadout r) => stat switch
    {
        "CPU Temperature" => new TryxOverlayLine(stat, $"{r.CpuTemp}°C"),
        "CPU Frequency" => new TryxOverlayLine(stat, $"{r.CpuClock}MHZ"),
        "CPU Usage" => new TryxOverlayLine(stat, $"{r.CpuLoad}%"),
        "CPU Voltage" => new TryxOverlayLine(stat, $"{r.CpuVoltage}V"),
        "GPU Temperature" => new TryxOverlayLine(stat, $"{r.GpuTemp}°C"),
        "GPU Frequency" => new TryxOverlayLine(stat, $"{r.GpuClock}MHZ"),
        "GPU Usage" => new TryxOverlayLine(stat, $"{r.GpuLoad}%"),
        "GPU Voltage" => new TryxOverlayLine(stat, $"{r.GpuVoltage}V"),
        "Motherboard Temperature" => new TryxOverlayLine(stat, $"{r.MoboTemp}°C"),
        "Memory Frequency" => new TryxOverlayLine(stat, $"{r.MemClock}MHZ"),
        "Memory Utilization" => new TryxOverlayLine(stat, $"{r.MemLoad}%"),
        "Date&Time" => new TryxOverlayLine(stat, DateTime.Now.ToString("HH:mm")),
        _ => null,
    };

    private List<TryxOverlayLine> BuildOverlayLines()
    {
        var r = ReadSensors();
        var lines = new List<TryxOverlayLine>(_overlay.Stats.Length);
        foreach (var stat in _overlay.Stats)
        {
            var line = MapOverlayStat(stat, in r);
            if (line is { } value)
            {
                lines.Add(value);
            }
        }
        return lines;
    }

    /// <summary>Paired (X,Y) entries from <see cref="TryxOverlayConfig.PosX"/>/PosY,
    /// truncated to whichever array is shorter; BuildOverlay defaults any stat past
    /// the end of this list to its fallback stack position.</summary>
    private List<(double X, double Y)> BuildOverlayPositions()
    {
        var count = Math.Min(_overlay.PosX.Length, _overlay.PosY.Length);
        var positions = new List<(double, double)>(count);
        for (var i = 0; i < count; i++)
        {
            positions.Add((_overlay.PosX[i], _overlay.PosY[i]));
        }
        return positions;
    }

    private static int ParseHexColorRgb(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length < 6)
        {
            return 0;
        }
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)) { r = 0; }
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)) { g = 0; }
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b)) { b = 0; }
        return (r << 16) | (g << 8) | b;
    }

    private IReadOnlyList<HardwareSensor> GetPrimaryGpuSensors()
    {
        // Match MonitoringBroadcaster: discrete-first ordering; use the first discrete GPU.
        var gpus = _sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static int RoundSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
        => (int)Math.Round(FindSensor(sensors, type, nameContains)?.Value ?? 0f);

    // Cores park independently; max is the headline clock across all matching sensors.
    private static int MaxClock(IReadOnlyList<HardwareSensor> sensors, string nameContains)
    {
        var max = 0f;
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (string.Equals(s.Type, "Clock", StringComparison.OrdinalIgnoreCase)
                && s.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                && s.Value > max)
            {
                max = s.Value;
            }
        }
        return (int)Math.Round(max);
    }

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null ||
                s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    private bool SendOnly(byte[] request)
    {
        // Every device write serializes on _txGate (reentrant), so a route-initiated control
        // frame (brightness/preset/overlay) cannot land between a transfer's BEGIN/DATA/COMMIT
        // frames - the transfer holds _txGate for its whole burst and this re-enters it.
        lock (_txGate)
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
            // The transport already retried a transient write on the held handle,
            // so reaching here is a persistent failure (e.g. the panel was
            // unplugged). Drop the transport so the next EnsureConnected rediscovers
            // and reopens - the retry-on-the-handle above is what prevents the old
            // reopen-on-every-transient-failure churn.
            ServiceLog.Error($"[tryx] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
        }
    }

    // One-shot commands (preset / brightness / screen / overlay-set) must survive the
    // panel's ~60s USB self-re-enumeration: a write that lands during the drop fails
    // and releases the transport; once the device re-appears (~0.2-1s) a retry via
    // EnsureConnected reopens it and succeeds. Without this a single user action that
    // coincides with a re-enumeration silently no-ops (and can leave the panel mid-
    // load). The 1 Hz heartbeat / overlay-refresh path uses SendOnly directly - it
    // re-sends on the next tick, so it needs no retry here.
    // After a re-enumeration drop the panel re-appears on the bus almost at once but
    // is not write-ready for another ~0.2-1s (observed: "write failed" -> "connected"
    // ~200ms later in the service log, and the reopen's config write can still fail
    // past that). Presence alone is a false ready-signal, so settle a fixed span
    // between resends rather than polling discovery. Six attempts span ~3s, longer
    // than any single recovery window seen.
    private const int SendMaxAttempts = 6;
    private const int SendRetrySettleMs = 500;

    private bool SendReliable(byte[] request)
    {
        for (var attempt = 0; attempt < SendMaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                if (_disposed) return false;
                Thread.Sleep(SendRetrySettleMs);
            }
            if (SendOnly(request)) return true;
        }
        return false;
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
            // Drain both pipes concurrently before waiting; a child that fills the
            // OS pipe buffer would otherwise deadlock against a read-after-wait.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                stderr = "timed out";
                return;
            }
            stdout = outTask.GetAwaiter().GetResult();
            stderr = errTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stderr = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

}
