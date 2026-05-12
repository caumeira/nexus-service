#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Displays;
using Qos.Service.Platform.Displays;
using Qos.Service.Serialization;

namespace Qos.Service.Helper;

/// <summary>
/// Helper-side dispatcher for service-to-helper commands. One switch keyed
/// on <see cref="HelperEnvelope.Type"/>. Adding a new command = adding one
/// case branch and the corresponding action (no envelope plumbing).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperClientCommands
{
    private readonly Action<bool> _setTrayVisible;
    private readonly Action<string, string>? _mediaControl;
    private readonly Func<string, byte[]>? _getAlbumArt;
    private readonly IDisplayBrightnessProvider? _brightness;

    public HelperClientCommands(
        Action<bool> setTrayVisible,
        Action<string, string>? mediaControl = null,
        Func<string, byte[]>? getAlbumArt = null,
        IDisplayBrightnessProvider? brightness = null)
    {
        _setTrayVisible = setTrayVisible;
        _mediaControl = mediaControl;
        _getAlbumArt = getAlbumArt;
        _brightness = brightness;
    }

    public Task<HelperResult> HandleAsync(HelperEnvelope env, CancellationToken ct)
    {
        try
        {
            switch (env.Type)
            {
                case "trayIcon.setVisible":
                    {
                        if (env.Payload is null) return Task.FromResult(Ok(env.Id));
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TraySetVisiblePayload);
                        _setTrayVisible(p?.Visible ?? false);
                        return Task.FromResult(Ok(env.Id));
                    }
                case "media.control":
                    {
                        if (_mediaControl is null) return Task.FromResult(Fail(env.Id, "media control not wired"));
                        if (env.Payload is null) return Task.FromResult(Ok(env.Id));
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.MediaControlPayload);
                        if (p is null) return Task.FromResult(Ok(env.Id));
                        _mediaControl(p.Source, p.Action);
                        return Task.FromResult(Ok(env.Id));
                    }
                case "media.getAlbumArt":
                    {
                        if (_getAlbumArt is null) return Task.FromResult(Fail(env.Id, "album art not wired"));
                        if (env.Payload is null) return Task.FromResult(WithBytes(env.Id, Array.Empty<byte>()));
                        var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AlbumArtRequest);
                        var bytes = p is null ? Array.Empty<byte>() : _getAlbumArt(p.Source);
                        return Task.FromResult(WithBytes(env.Id, bytes));
                    }
                case "displayBrightness.hint":
                    if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                    return Task.FromResult(WithJson(env.Id,
                        new StringResult { Value = _brightness.Hint },
                        AppJsonContext.Default.StringResult));
                case "displayBrightness.enumerate":
                    if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                    return Task.FromResult(WithJson(env.Id,
                        new DisplayListResult { Displays = new(_brightness.Enumerate()) },
                        AppJsonContext.Default.DisplayListResult));
                case "displayBrightness.get":
                    {
                        if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                        var p = ReadBrightnessReq(env);
                        var v = _brightness.GetBrightness(p.Id);
                        return Task.FromResult(WithJson(env.Id,
                            new NullableIntResult { Value = v ?? -1, HasValue = v.HasValue },
                            AppJsonContext.Default.NullableIntResult));
                    }
                case "displayBrightness.set":
                    {
                        if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                        var p = ReadBrightnessReq(env);
                        var dto = _brightness.SetBrightness(p.Id, p.Percent);
                        return Task.FromResult(WithJson(env.Id, dto,
                            AppJsonContext.Default.DisplayBrightnessDto));
                    }
                case "displayBrightness.policy":
                    {
                        if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                        var p = ReadBrightnessReq(env);
                        var policy = _brightness.GetBrightnessWritePolicy(p.Id);
                        return Task.FromResult(WithJson(env.Id, policy,
                            AppJsonContext.Default.DisplayBrightnessWritePolicy));
                    }
                case "displayBrightness.getVcp":
                    {
                        if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                        var p = ReadBrightnessReq(env);
                        var dto = _brightness.GetVcp(p.Id, p.Code);
                        return Task.FromResult(WithJson(env.Id,
                            new DisplayVcpResult { Ok = dto is not null, Dto = dto },
                            AppJsonContext.Default.DisplayVcpResult));
                    }
                case "displayBrightness.setVcp":
                    {
                        if (_brightness is null) return Task.FromResult(Fail(env.Id, "brightness not wired"));
                        var p = ReadBrightnessReq(env);
                        var ok = _brightness.SetVcp(p.Id, p.Code, p.Value);
                        return Task.FromResult(WithJson(env.Id,
                            new BoolResult { Value = ok },
                            AppJsonContext.Default.BoolResult));
                    }
                default:
                    return Task.FromResult(Fail(env.Id, $"unknown type {env.Type}"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(env.Id, ex.Message));
        }
    }

    private static HelperResult Ok(string? id) => new() { Id = id ?? "", Ok = true };
    private static HelperResult Fail(string? id, string error) => new() { Id = id ?? "", Ok = false, Error = error };
    private static HelperResult WithBytes(string? id, byte[] bytes) => new()
    {
        Id = id ?? "",
        Ok = true,
        Payload = JsonSerializer.SerializeToElement(
            new AlbumArtResult { Bytes = bytes },
            AppJsonContext.Default.AlbumArtResult),
    };
    private static HelperResult WithJson<T>(string? id, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => new()
    {
        Id = id ?? "",
        Ok = true,
        Payload = JsonSerializer.SerializeToElement(value, typeInfo),
    };
    private static DisplayBrightnessRequest ReadBrightnessReq(HelperEnvelope env)
    {
        if (env.Payload is null) return new DisplayBrightnessRequest();
        return JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.DisplayBrightnessRequest)
               ?? new DisplayBrightnessRequest();
    }
}
#endif
