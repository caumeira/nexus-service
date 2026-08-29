using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>
    /// Registrations for hardware the bundled daemon cannot detect on its own:
    /// QMK-OpenRGB keyboards (found only by the vid/pid they were flashed with)
    /// and E1.31 / WLED devices (a bare IP). Both are what OpenRGB's own GUI
    /// stores, so the import adopts an existing install's lists wholesale.
    ///
    /// Every mutation bounces the RGB subprocess: the daemon reads its config
    /// once at launch, so a registration is inert until it restarts.
    /// </summary>
    private static void MapOpenRgbManualDeviceEndpoints(WebApplication app)
    {
        app.MapGet("/devices/openrgb/manual-devices", (IConfigStore store) =>
        {
            var devices = store.Load().Devices.OpenRgbManualDevices;
            var sourcePath = OpenRgbConfigImport.DefaultSourcePath();
            var response = new OpenRgbManualDevicesResponse
            {
                ImportSourcePath = sourcePath,
                ImportSourceAvailable = System.IO.File.Exists(sourcePath),
            };
            foreach (var e in devices.Qmk)
            {
                response.Qmk.Add(new QmkDeviceDto { Name = e.Name, UsbVid = e.UsbVid, UsbPid = e.UsbPid });
            }
            foreach (var e in devices.E131)
            {
                response.E131.Add(new E131DeviceDto
                {
                    Name = e.Name, Ip = e.Ip, NumLeds = e.NumLeds,
                    StartUniverse = e.StartUniverse, StartChannel = e.StartChannel,
                    KeepaliveTime = e.KeepaliveTime, UniverseSize = e.UniverseSize,
                });
            }
            return Results.Json(response, AppJsonContext.Default.OpenRgbManualDevicesResponse);
        });

        app.MapPost("/devices/openrgb/manual-devices/qmk", (AddQmkDeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            if (string.IsNullOrWhiteSpace(body.UsbVid) || string.IsNullOrWhiteSpace(body.UsbPid))
            {
                return Results.BadRequest(ApiResponse.Fail("usb_vid and usb_pid are required"));
            }
            var vid = OpenRgbManualDeviceConfig.NormalizeHex(body.UsbVid);
            var pid = OpenRgbManualDeviceConfig.NormalizeHex(body.UsbPid);
            if (!IsHex(vid) || !IsHex(pid))
            {
                return Results.BadRequest(ApiResponse.Fail("usb_vid and usb_pid must be hex, e.g. 3434 and 0660"));
            }
            store.Update(s =>
            {
                var list = s.Devices.OpenRgbManualDevices.Qmk;
                foreach (var e in list)
                {
                    if (string.Equals(e.UsbVid, vid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.UsbPid, pid, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Name = body.Name;
                        return;
                    }
                }
                list.Add(new QmkOpenRgbDeviceEntry { Name = body.Name, UsbVid = vid, UsbPid = pid });
            });
            bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/e131", (AddE131DeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            if (string.IsNullOrWhiteSpace(body.Ip))
            {
                return Results.BadRequest(ApiResponse.Fail("ip is required"));
            }
            store.Update(s =>
            {
                var list = s.Devices.OpenRgbManualDevices.E131;
                foreach (var e in list)
                {
                    if (string.Equals(e.Ip, body.Ip, StringComparison.OrdinalIgnoreCase) && e.StartUniverse == body.StartUniverse)
                    {
                        e.Name = body.Name;
                        e.NumLeds = body.NumLeds;
                        e.StartChannel = body.StartChannel;
                        e.KeepaliveTime = body.KeepaliveTime;
                        e.UniverseSize = body.UniverseSize;
                        return;
                    }
                }
                list.Add(new E131DeviceEntry
                {
                    Name = body.Name, Ip = body.Ip, NumLeds = body.NumLeds,
                    StartUniverse = body.StartUniverse, StartChannel = body.StartChannel,
                    KeepaliveTime = body.KeepaliveTime, UniverseSize = body.UniverseSize,
                });
            });
            bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/remove", (RemoveManualDeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            store.Update(s =>
            {
                var devices = s.Devices.OpenRgbManualDevices;
                if (string.Equals(body.Kind, "qmk", StringComparison.OrdinalIgnoreCase))
                {
                    var vid = OpenRgbManualDeviceConfig.NormalizeHex(body.Key);
                    var pid = OpenRgbManualDeviceConfig.NormalizeHex(body.Key2);
                    devices.Qmk.RemoveAll(e =>
                        string.Equals(e.UsbVid, vid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.UsbPid, pid, StringComparison.OrdinalIgnoreCase));
                }
                else if (string.Equals(body.Kind, "e131", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(body.Key2, out var universe);
                    devices.E131.RemoveAll(e =>
                        string.Equals(e.Ip, body.Key, StringComparison.OrdinalIgnoreCase)
                        && (body.Key2.Length == 0 || e.StartUniverse == universe));
                }
            });
            bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/import", (ImportOpenRgbConfigBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            var path = string.IsNullOrWhiteSpace(body.Path) ? OpenRgbConfigImport.DefaultSourcePath() : body.Path;
            var imported = OpenRgbConfigImport.Read(path);
            var added = 0;
            if (imported.Found)
            {
                store.Update(s => added = OpenRgbConfigImport.Merge(s.Devices.OpenRgbManualDevices, imported));
                if (added > 0) bridge?.BounceForManualDevices();
            }
            return Results.Json(new ImportOpenRgbConfigResponse
            {
                SourceFound = imported.Found,
                Path = path,
                Added = added,
                QmkSeen = imported.Qmk.Count,
                E131Seen = imported.E131.Count,
            }, AppJsonContext.Default.ImportOpenRgbConfigResponse);
        });
    }

    private static bool IsHex(string s)
    {
        if (s.Length == 0 || s.Length > 4) return false;
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
