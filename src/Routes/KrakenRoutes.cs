using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapKrakenEndpoints(WebApplication app)
    {
        // GET /devices/nzxt-kraken/state
        app.MapGet("/devices/nzxt-kraken/state", (KrakenHub hub) =>
        {
            // Read the snapshot once; every field below uses this one reference.
            var snap = hub.Snapshot;
            var channels = new KrakenChannelDto[snap.Channels.Count];
            for (var i = 0; i < snap.Channels.Count; i++)
            {
                var c = snap.Channels[i];
                channels[i] = new KrakenChannelDto
                {
                    Id = KrakenHub.ZoneIdForChannelIndex(i),
                    AccessoryName = c.AccessoryName,
                    LedCount = c.LedCount,
                };
            }
            return Results.Json(
                new KrakenStateResponse
                {
                    IsConnected = hub.IsConnected,
                    FirmwareVersion = snap.FirmwareVersion,
                    LiquidTempC = Math.Round(snap.LiquidTempC, 1),
                    PumpRpm = snap.PumpRpm,
                    PumpDuty = snap.PumpDuty,
                    FanRpm = snap.FanRpm,
                    FanDuty = snap.FanDuty,
                    HasLcd = hub.HasLcd,
                    LcdWidth = KrakenProtocol.LcdWidth,
                    LcdHeight = KrakenProtocol.LcdHeight,
                    LcdBrightness = snap.LcdBrightness,
                    LcdOrientation = snap.LcdOrientationQuarterTurns * 90,
                    LcdMode = snap.DisplayMode switch
                    {
                        KrakenDisplayMode.Blank => "off",
                        KrakenDisplayMode.Bucket => "image",
                        _ => "liquid",
                    },
                    Channels = channels,
                },
                AppJsonContext.Default.KrakenStateResponse);
        });

        // PUT /devices/nzxt-kraken/lcd - backlight, rotation and display mode.
        app.MapPut("/devices/nzxt-kraken/lcd", (KrakenLcdRequest body, KrakenHub hub) =>
        {
            if (!hub.IsConnected)
            {
                return Results.BadRequest(ApiResponse.Fail("kraken not connected"));
            }

            var snap = hub.Snapshot;
            if (body.Brightness.HasValue || body.Orientation.HasValue)
            {
                if (body.Brightness is { } b && (b < 0 || b > 100))
                {
                    return Results.BadRequest(ApiResponse.Fail("brightness must be 0-100"));
                }
                int quarterTurns = snap.LcdOrientationQuarterTurns;
                if (body.Orientation is { } deg)
                {
                    if (deg != 0 && deg != 90 && deg != 180 && deg != 270)
                    {
                        return Results.BadRequest(ApiResponse.Fail("orientation must be 0, 90, 180 or 270"));
                    }
                    quarterTurns = deg / 90;
                }
                // Backlight and rotation share one command, so both always go together.
                if (!hub.SetLcdBacklight(body.Brightness ?? snap.LcdBrightness, quarterTurns))
                {
                    return Results.BadRequest(ApiResponse.Fail("failed to set lcd backlight"));
                }
            }

            if (body.Mode != null)
            {
                var mode = body.Mode switch
                {
                    "off" => KrakenDisplayMode.Blank,
                    "liquid" => KrakenDisplayMode.Liquid,
                    "image" => KrakenDisplayMode.Bucket,
                    _ => (KrakenDisplayMode?)null,
                };
                if (mode == null)
                {
                    return Results.BadRequest(ApiResponse.Fail("mode must be off, liquid or image"));
                }
                if (!hub.SetDisplayMode(mode.Value))
                {
                    return Results.BadRequest(ApiResponse.Fail("failed to set lcd mode"));
                }
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/nzxt-kraken/lcd/image - raw RGBA body, exactly one LCD frame.
        app.MapPost("/devices/nzxt-kraken/lcd/image", async (HttpRequest request, KrakenHub hub) =>
        {
            if (!hub.IsConnected)
            {
                return Results.BadRequest(ApiResponse.Fail("kraken not connected"));
            }
            if (!hub.HasLcd)
            {
                return Results.BadRequest(ApiResponse.Fail("lcd bulk pipe unavailable"));
            }

            var expected = KrakenProtocol.LcdFrameBytes;
            var buffer = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = await request.Body.ReadAsync(buffer.AsMemory(read, expected - read)).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            if (read != expected)
            {
                return Results.BadRequest(ApiResponse.Fail(
                    $"expected {expected} bytes of RGBA ({KrakenProtocol.LcdWidth}x{KrakenProtocol.LcdHeight}), got {read}"));
            }
            // Guard against a body longer than one frame rather than silently truncating.
            if (await request.Body.ReadAsync(new byte[1].AsMemory(0, 1)).ConfigureAwait(false) != 0)
            {
                return Results.BadRequest(ApiResponse.Fail($"body longer than one {expected}-byte frame"));
            }

            return hub.UploadLcdImage(buffer)
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.BadRequest(ApiResponse.Fail("lcd upload rejected by device"));
        });

        // GET /devices/nzxt-kraken/firmware-lighting - the animations the cooler can play
        // on its own, plus what was last written to each channel. The cooler cannot be
        // asked what it is playing, so the per-channel values come from settings.
        app.MapGet("/devices/nzxt-kraken/firmware-lighting", (KrakenHub hub, IConfigStore store) =>
        {
            var settings = store.Load();
            var saved = settings.Devices.KrakenFirmwareLighting;
            var uncontrolled = settings.Devices.UncontrolledLightingDevices;

            var effects = new KrakenEffectDto[KrakenEffects.All.Length];
            for (var i = 0; i < KrakenEffects.All.Length; i++)
            {
                var e = KrakenEffects.All[i];
                effects[i] = new KrakenEffectDto
                {
                    Id = e.Id,
                    MinColors = e.MinColors,
                    MaxColors = e.MaxColors,
                    Directional = e.Directional,
                };
            }

            var snap = hub.Snapshot;
            var channels = new KrakenFirmwareChannelDto[snap.Channels.Count];
            for (var i = 0; i < snap.Channels.Count; i++)
            {
                var zoneId = KrakenHub.ZoneIdForChannelIndex(i);
                saved.TryGetValue(zoneId, out var cfg);
                channels[i] = new KrakenFirmwareChannelDto
                {
                    Id = zoneId,
                    AccessoryName = snap.Channels[i].AccessoryName,
                    Effect = cfg?.Effect ?? "fixed",
                    Speed = cfg?.Speed ?? 2,
                    Forward = cfg?.Forward ?? true,
                    Colors = cfg?.Colors?.ToArray() ?? new[] { "#ff0000" },
                    NexusDriven = !uncontrolled.Contains(zoneId),
                };
            }

            return Results.Json(
                new KrakenFirmwareLightingResponse
                {
                    IsConnected = hub.IsConnected,
                    Effects = effects,
                    Channels = channels,
                },
                AppJsonContext.Default.KrakenFirmwareLightingResponse);
        });

        // PUT /devices/nzxt-kraken/firmware-lighting - write one channel's animation.
        app.MapPut("/devices/nzxt-kraken/firmware-lighting", (KrakenFirmwareLightingRequest body, KrakenHub hub, IConfigStore store) =>
        {
            if (!hub.IsConnected)
            {
                return Results.Conflict(ApiResponse.Fail("kraken not connected"));
            }

            // One snapshot for both the lookup and the channel id: re-reading hub.Snapshot
            // lets a detach empty the list between them and index past the end.
            var channels = hub.Snapshot.Channels;
            var channelIndex = IndexOfChannel(channels, body.Channel);
            if (channelIndex < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown channel"));
            }

            var effect = KrakenEffects.Find(body.Effect);
            if (effect is null)
            {
                return Results.BadRequest(ApiResponse.Fail($"unknown effect '{body.Effect}'"));
            }

            var hex = body.Colors ?? Array.Empty<string>();
            if (hex.Length < effect.MinColors || hex.Length > Math.Max(effect.MaxColors, effect.MinColors))
            {
                return Results.BadRequest(ApiResponse.Fail(
                    $"effect '{effect.Id}' takes {effect.MinColors} to {effect.MaxColors} colours, got {hex.Length}"));
            }

            var rgb = new byte[hex.Length * 3];
            for (var i = 0; i < hex.Length; i++)
            {
                if (!TryParseHexColor(hex[i], out var r, out var g, out var b))
                {
                    return Results.BadRequest(ApiResponse.Fail($"colour '{hex[i]}' is not #rrggbb"));
                }
                rgb[i * 3] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }

            var speed = (KrakenAnimationSpeed)Math.Clamp(body.Speed, 0, 4);
            var channelId = channels[channelIndex].ChannelId;
            if (!hub.SetLighting(channelId, effect.Mode, speed, rgb, body.Forward))
            {
                return Results.Problem("Failed to write the animation to the cooler.");
            }

            var zoneId = KrakenHub.ZoneIdForChannelIndex(channelIndex);
            store.Update(s =>
            {
                s.Devices.KrakenFirmwareLighting[zoneId] = new KrakenFirmwareLighting
                {
                    Effect = effect.Id,
                    Speed = (int)speed,
                    Forward = body.Forward,
                    Colors = new List<string>(hex),
                };
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static int IndexOfChannel(IReadOnlyList<KrakenLightingChannel> channels, string? zoneId)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            if (string.Equals(KrakenHub.ZoneIdForChannelIndex(i), zoneId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool TryParseHexColor(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text[0] == '#')
        {
            text = text.Substring(1);
        }
        if (text.Length != 6)
        {
            return false;
        }
        return byte.TryParse(text.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && byte.TryParse(text.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && byte.TryParse(text.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}

public sealed class KrakenChannelDto
{
    public string Id { get; set; } = "";
    public string AccessoryName { get; set; } = "";
    public int LedCount { get; set; }
}

public sealed class KrakenStateResponse
{
    public bool IsConnected { get; set; }
    public string FirmwareVersion { get; set; } = "";
    public double LiquidTempC { get; set; }
    public int PumpRpm { get; set; }
    public int PumpDuty { get; set; }
    public int FanRpm { get; set; }
    public int FanDuty { get; set; }
    public bool HasLcd { get; set; }
    public int LcdWidth { get; set; }
    public int LcdHeight { get; set; }
    public int LcdBrightness { get; set; }
    public int LcdOrientation { get; set; }
    public string LcdMode { get; set; } = "";
    public KrakenChannelDto[] Channels { get; set; } = Array.Empty<KrakenChannelDto>();
}

public sealed class KrakenLcdRequest
{
    public int? Brightness { get; set; }
    public int? Orientation { get; set; }
    public string? Mode { get; set; }
}

public sealed class KrakenEffectDto
{
    public string Id { get; set; } = "";
    public int MinColors { get; set; }
    public int MaxColors { get; set; }
    public bool Directional { get; set; }
}

public sealed class KrakenFirmwareChannelDto
{
    public string Id { get; set; } = "";
    public string AccessoryName { get; set; } = "";
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public bool Forward { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    /// <summary>True while Nexus still pushes frames to this channel, which overrides the animation.</summary>
    public bool NexusDriven { get; set; }
}

public sealed class KrakenFirmwareLightingResponse
{
    public bool IsConnected { get; set; }
    public KrakenEffectDto[] Effects { get; set; } = Array.Empty<KrakenEffectDto>();
    public KrakenFirmwareChannelDto[] Channels { get; set; } = Array.Empty<KrakenFirmwareChannelDto>();
}

public sealed class KrakenFirmwareLightingRequest
{
    public string Channel { get; set; } = "";
    public string Effect { get; set; } = "";
    public int Speed { get; set; } = 2;
    public bool Forward { get; set; } = true;
    public string[]? Colors { get; set; }
}
