using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Discovers ArtInChip D213 boards over adb. The board's serial always starts
/// with "d211_" (SoC family <c>artinchip,d211</c>); it drops off the USB bus
/// on host power/network events and is expected to re-enumerate, so absence
/// is a normal tick result rather than an error.
/// </summary>
public sealed class D213PanelDiscovery : IStreamedPanelDiscovery
{
    private const string SerialPrefix = "d211_";

    private readonly StreamedPanelStore _store;
    private readonly AdbClient _client;

    private bool _serverUnavailableLogged;

    public D213PanelDiscovery(StreamedPanelStore store)
    {
        _store = store;
        _client = new AdbClient();
    }

    public string HandlerId => "artinchip-d213";

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        IEnumerable<DeviceData> devices;
        try
        {
            devices = _client.GetDevices();
        }
        catch (SocketException)
        {
            if (!TryStartAdbServer())
            {
                LogServerUnavailableOnce();
                return Array.Empty<StreamedPanelDeviceInfo>();
            }
            try
            {
                devices = _client.GetDevices();
            }
            catch
            {
                LogServerUnavailableOnce();
                return Array.Empty<StreamedPanelDeviceInfo>();
            }
        }

        _serverUnavailableLogged = false;

        var records = _store.Load();
        var result = new List<StreamedPanelDeviceInfo>();
        foreach (var device in devices)
        {
            if (string.IsNullOrEmpty(device.Serial))
            {
                continue;
            }
            if (device.State != DeviceState.Online)
            {
                continue;
            }
            if (!device.Serial.StartsWith(SerialPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            records.TryGetValue(device.Serial, out var record);
            result.Add(new StreamedPanelDeviceInfo
            {
                Serial = device.Serial,
                Profile = D213Profiles.Resolve(record),
            });
        }
        return result;
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) => new AdbStreamTransport(info);

    private bool TryStartAdbServer()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "start-server",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(8_000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void LogServerUnavailableOnce()
    {
        if (_serverUnavailableLogged)
        {
            return;
        }
        _serverUnavailableLogged = true;
        ServiceLog.Info("[d213] adb-server unavailable; will retry on next discovery tick");
    }
}

/// <summary>
/// Profile table for the D213's two curated device kinds, plus the per-serial
/// override merge from <see cref="StreamedPanelRecord"/>. Pure and adb-free so
/// kind selection and fps/bitrate overrides are testable without hardware.
/// </summary>
internal static class D213Profiles
{
    public const string FsKind = "d213-fs";
    public const string Q60Kind = "d213-q60";

    /// <summary>Default profile for a kind. Any kind other than <see cref="Q60Kind"/> falls back to <see cref="FsKind"/>.</summary>
    public static StreamedPanelProfile Default(string kind)
    {
        if (kind == Q60Kind)
        {
            return new StreamedPanelProfile
            {
                Kind = Q60Kind,
                DisplayName = "ArtInChip D213 (square)",
                Surface = PanelSurfaces.Q60,
                CssWidth = 800,
                CssHeight = 800,
                Dpr = 1.0,
                Fps = 60,
                BitrateKbps = 8000,
            };
        }

        return new StreamedPanelProfile
        {
            Kind = FsKind,
            DisplayName = "ArtInChip D213",
            Surface = PanelSurfaces.Monitor,
            CssWidth = 1024,
            CssHeight = 600,
            Dpr = 1.0,
            Fps = 60,
            BitrateKbps = 8000,
        };
    }

    /// <summary>Applies a per-serial <see cref="StreamedPanelRecord"/> override on top of its kind's default profile.</summary>
    public static StreamedPanelProfile Resolve(StreamedPanelRecord? record)
    {
        var kind = record?.ProfileKind ?? FsKind;
        var basis = Default(kind);
        if (record is null || (record.Fps is null && record.BitrateKbps is null))
        {
            return basis;
        }

        return new StreamedPanelProfile
        {
            Kind = basis.Kind,
            DisplayName = basis.DisplayName,
            Surface = basis.Surface,
            CssWidth = basis.CssWidth,
            CssHeight = basis.CssHeight,
            Dpr = basis.Dpr,
            Fps = record.Fps ?? basis.Fps,
            BitrateKbps = record.BitrateKbps ?? basis.BitrateKbps,
        };
    }
}
