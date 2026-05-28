using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Orchestrates a full firmware flash, encoding the sequence proven on hardware
/// (CNVS, 2026-05-28): ensure WinUSB driver → in-app DFU entry (hub writes key +
/// magic, releases its COM port) → wait for the bootloader to enumerate as
/// 3402:0a00 → dfu-util download at 0x0800C000 → readback-verify → flag-erase
/// (0xFF @ 0x0801FFF0) + leave. One flash at a time; progress lives in
/// <see cref="Status"/> so the UI survives navigation, and <see cref="IsFlashing"/>
/// blocks app shutdown.
/// </summary>
public sealed class FirmwareFlasher
{
    private readonly BundledFirmwareCatalog _catalog;
    private readonly IReadOnlyList<IDfuFlashTarget> _targets;
    private readonly WinUsbDriverInstaller _winusb;
    private readonly DfuUtil _dfu;
    private readonly object _gate = new();
    private volatile bool _flashing;

    public FirmwareFlasher(
        BundledFirmwareCatalog catalog,
        IEnumerable<IDfuFlashTarget> targets,
        WinUsbDriverInstaller winusb,
        DfuUtil dfu)
    {
        _catalog = catalog;
        _targets = targets.ToList();
        _winusb = winusb;
        _dfu = dfu;
    }

    public FlashStatusDto Status { get; } = new();
    public bool IsFlashing => _flashing;

    /// <summary>
    /// Begin a flash on a background task. Returns false (with a reason) if one
    /// is already running, the image isn't bundled, or no connected device can
    /// take it. Validates everything up front so the caller gets immediate feedback.
    /// </summary>
    public bool TryStart(string deviceType, string version, out string error)
    {
        lock (_gate)
        {
            if (_flashing) { error = "A firmware update is already in progress."; return false; }
            if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(version))
            { error = "deviceType and version are required."; return false; }
            if (!_catalog.GetAvailableVersions(deviceType).Contains(version))
            { error = $"No bundled firmware for {deviceType} {version}."; return false; }
            var target = _targets.FirstOrDefault(t => t.IsConnected && t.CanFlash(deviceType));
            if (target is null) { error = $"No connected device can flash {deviceType}."; return false; }

            _flashing = true;
            Status.Active = true;
            Status.DeviceType = deviceType;
            Status.Version = version;
            Status.Phase = "preparing";
            Status.Percent = 0;
            Status.Message = "Preparing…";
            Status.Success = false;
            Status.Error = "";
            error = "";
            _ = Task.Run(() => RunAsync(deviceType, version, target));
            return true;
        }
    }

    private async Task RunAsync(string deviceType, string version, IDfuFlashTarget target)
    {
        string? binPath = null;
        string? flagPath = null;
        string? readbackPath = null;
        try
        {
            // 1. Resolve + convert the bundled image to a flat bin.
            Set("preparing", 5, "Reading firmware image…");
            byte[] bin;
            using (var hex = _catalog.OpenFirmware(deviceType, version)
                   ?? throw new InvalidOperationException($"firmware {deviceType}/{version} missing"))
            {
                bin = IntelHex.Parse(hex).Data;
            }
            binPath = Path.Combine(Path.GetTempPath(), $"nexus-fw-{deviceType}-{version}.bin");
            await File.WriteAllBytesAsync(binPath, bin);

            // 2. Stage the WinUSB driver so the DFU device is openable.
            Set("preparing", 10, "Preparing USB driver…");
            await _winusb.EnsureInstalledAsync(CancellationToken.None);

            // 3. In-app DFU entry (hub writes key+magic, releases its COM port).
            Set("entering-dfu", 15, "Switching device to update mode…");
            if (!target.EnterDfuMode())
            {
                Fail("Could not switch the device into update mode.");
                return;
            }

            // 4. Wait for the bootloader to enumerate as 3402:0a00.
            Set("waiting-dfu", 25, "Waiting for bootloader…");
            if (!await WaitForDfuAsync(TimeSpan.FromSeconds(20)))
            {
                Fail("Device did not appear in update mode (DFU).");
                return;
            }

            // 5. Download the app image at 0x0800C000 (no leave yet).
            Set("downloading", 40, "Writing firmware…");
            var dl = await _dfu.DownloadAsync(binPath, DfuUtil.AppBaseAddress, leave: false, CancellationToken.None);
            if (!dl.Success) { Fail($"Flash failed: {Tail(dl.Output)}"); return; }

            // 6. Read back + verify byte-for-byte.
            Set("verifying", 70, "Verifying…");
            readbackPath = Path.Combine(Path.GetTempPath(), $"nexus-fw-{deviceType}-readback.bin");
            var up = await _dfu.UploadAsync(readbackPath, DfuUtil.AppBaseAddress, bin.Length, CancellationToken.None);
            if (!up.Success) { Fail($"Verify read-back failed: {Tail(up.Output)}"); return; }
            var readback = await File.ReadAllBytesAsync(readbackPath);
            if (!readback.AsSpan().SequenceEqual(bin))
            {
                Fail("Verification mismatch — flashed image does not match the source.");
                return;
            }

            // 7. Erase the boot flag (0xFF @ 0x0801FFF0) + leave -> boot the app.
            Set("finalizing", 90, "Finalizing…");
            flagPath = Path.Combine(Path.GetTempPath(), "nexus-fw-flag.bin");
            await File.WriteAllBytesAsync(flagPath, Enumerable.Repeat((byte)0xFF, 16).ToArray());
            // dfu-util reports a get_status error on :leave (device detaches before
            // the final status read) — that's expected, so don't treat it as failure.
            await _dfu.DownloadAsync(flagPath, DfuUtil.BootFlagAddress, leave: true, CancellationToken.None);

            Status.Success = true;
            Set("done", 100, $"Updated to {version}.");
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(binPath); TryDelete(flagPath); TryDelete(readbackPath);
            Status.Active = false;
            _flashing = false;
        }
    }

    private async Task<bool> WaitForDfuAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var r = await _dfu.ListAsync(CancellationToken.None);
            if (DfuUtil.CountDfuDevices(r.Output) > 0) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    private void Set(string phase, int percent, string message)
    {
        Status.Phase = phase;
        Status.Percent = percent;
        Status.Message = message;
    }

    private void Fail(string error)
    {
        Status.Phase = "failed";
        Status.Success = false;
        Status.Error = error;
        Status.Message = error;
    }

    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.TrimEnd().Split('\n');
        return lines[^1].Trim();
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
