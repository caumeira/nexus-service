#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Displays;
using Qos.Service.Platform.Displays;
using Qos.Service.Serialization;

namespace Qos.Service.Helper.Domains
{
    /// <summary>
    /// Combined request payload for the <c>displayBrightness.*</c> commands.
    /// Method-specific fields are tolerant of being unset; the helper
    /// handler only reads the ones it needs per type. Kept as one shared
    /// god-DTO because the existing wire format is in use and changing it
    /// would require a coordinated helper roll.
    /// </summary>
    public sealed class DisplayBrightnessRequest
    {
        public string Id { get; set; } = "";
        public int Percent { get; set; }
        public byte Code { get; set; }
        public int Value { get; set; }
    }

    /// <summary>Response wrapper for a single string (<c>displayBrightness.hint</c>).</summary>
    public sealed class StringResult
    {
        public string Value { get; set; } = "";
    }

    /// <summary>Response wrapper for nullable int (-1 means null).</summary>
    public sealed class NullableIntResult
    {
        public int Value { get; set; } = -1;
        public bool HasValue { get; set; }
    }

    /// <summary>Response wrapper for nullable DisplayVcpDto (Ok=false means null).</summary>
    public sealed class DisplayVcpResult
    {
        public bool Ok { get; set; }
        public DisplayVcpDto? Dto { get; set; }
    }

    /// <summary>Response wrapper for a single boolean.</summary>
    public sealed class BoolResult
    {
        public bool Value { get; set; }
    }

    /// <summary>Response wrapper for the <c>displayBrightness.enumerate</c> list.</summary>
    public sealed class DisplayListResult
    {
        public List<DisplayDto> Displays { get; set; } = new();
    }

    // JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

    /// <summary>
    /// Service-side outbound facade for brightness RPCs. Each method blocks
    /// on the pipe for the helper's reply (or the configured 4 s timeout)
    /// and returns sensible defaults when no helper is connected. The
    /// IDisplayBrightnessProvider proxy in <c>Platform/Displays/</c> calls
    /// these via synchronous <c>.GetAwaiter().GetResult()</c> because the
    /// interface contract is sync; HTTP route handlers tolerate the wait.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class BrightnessCommands
    {
        public static async Task<string> HintAsync(HelperRegistry r, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.hint", new DisplayBrightnessRequest(), ct).ConfigureAwait(false);
            return Read(res, AppJsonContext.Default.StringResult)?.Value ?? "";
        }

        public static async Task<IReadOnlyList<DisplayDto>> EnumerateAsync(HelperRegistry r, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.enumerate", new DisplayBrightnessRequest(), ct).ConfigureAwait(false);
            return Read(res, AppJsonContext.Default.DisplayListResult)?.Displays ?? new List<DisplayDto>();
        }

        public static async Task<int?> GetAsync(HelperRegistry r, string id, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.get", new DisplayBrightnessRequest { Id = id }, ct).ConfigureAwait(false);
            var dto = Read(res, AppJsonContext.Default.NullableIntResult);
            return dto is null || !dto.HasValue ? null : dto.Value;
        }

        public static async Task<DisplayBrightnessDto> SetAsync(HelperRegistry r, string id, int percent, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.set", new DisplayBrightnessRequest { Id = id, Percent = percent }, ct).ConfigureAwait(false);
            return Read(res, AppJsonContext.Default.DisplayBrightnessDto) ?? new DisplayBrightnessDto();
        }

        public static async Task<DisplayBrightnessWritePolicy> PolicyAsync(HelperRegistry r, string id, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.policy", new DisplayBrightnessRequest { Id = id }, ct).ConfigureAwait(false);
            return Read(res, AppJsonContext.Default.DisplayBrightnessWritePolicy) ?? new DisplayBrightnessWritePolicy();
        }

        public static async Task<DisplayVcpDto?> GetVcpAsync(HelperRegistry r, string id, byte code, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.getVcp", new DisplayBrightnessRequest { Id = id, Code = code }, ct).ConfigureAwait(false);
            var dto = Read(res, AppJsonContext.Default.DisplayVcpResult);
            return dto is null || !dto.Ok ? null : dto.Dto;
        }

        public static async Task<bool> SetVcpAsync(HelperRegistry r, string id, byte code, int value, CancellationToken ct = default)
        {
            var res = await InvokeAsync(r, "displayBrightness.setVcp", new DisplayBrightnessRequest { Id = id, Code = code, Value = value }, ct).ConfigureAwait(false);
            return Read(res, AppJsonContext.Default.BoolResult)?.Value ?? false;
        }

        private static async Task<HelperResult?> InvokeAsync(HelperRegistry r, string type, DisplayBrightnessRequest payload, CancellationToken ct)
        {
            var conn = r.GetAny();
            if (conn is null) return null;
            return await conn.SendCommandAsync(type, payload, AppJsonContext.Default.DisplayBrightnessRequest, timeoutMs: 4000, ct: ct).ConfigureAwait(false);
        }

        private static T? Read<T>(HelperResult? r, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        {
            if (r is null || !r.Ok || r.Payload is null) return default;
            try { return JsonSerializer.Deserialize(r.Payload.Value, typeInfo); }
            catch { return default; }
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class BrightnessHandler
    {
        private readonly IDisplayBrightnessProvider _provider;

        public BrightnessHandler(IDisplayBrightnessProvider provider) { _provider = provider; }

        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register("displayBrightness.hint", (env, _) => Reply(env,
                new StringResult { Value = _provider.Hint },
                AppJsonContext.Default.StringResult));

            registry.Register("displayBrightness.enumerate", (env, _) => Reply(env,
                new DisplayListResult { Displays = new(_provider.Enumerate()) },
                AppJsonContext.Default.DisplayListResult));

            registry.Register("displayBrightness.get", (env, _) =>
            {
                var p = ReadReq(env);
                var v = _provider.GetBrightness(p.Id);
                return Reply(env, new NullableIntResult { Value = v ?? -1, HasValue = v.HasValue },
                    AppJsonContext.Default.NullableIntResult);
            });

            registry.Register("displayBrightness.set", (env, _) =>
            {
                var p = ReadReq(env);
                var dto = _provider.SetBrightness(p.Id, p.Percent);
                return Reply(env, dto, AppJsonContext.Default.DisplayBrightnessDto);
            });

            registry.Register("displayBrightness.policy", (env, _) =>
            {
                var p = ReadReq(env);
                var policy = _provider.GetBrightnessWritePolicy(p.Id);
                return Reply(env, policy, AppJsonContext.Default.DisplayBrightnessWritePolicy);
            });

            registry.Register("displayBrightness.getVcp", (env, _) =>
            {
                var p = ReadReq(env);
                var dto = _provider.GetVcp(p.Id, p.Code);
                return Reply(env, new DisplayVcpResult { Ok = dto is not null, Dto = dto },
                    AppJsonContext.Default.DisplayVcpResult);
            });

            registry.Register("displayBrightness.setVcp", (env, _) =>
            {
                var p = ReadReq(env);
                var ok = _provider.SetVcp(p.Id, p.Code, p.Value);
                return Reply(env, new BoolResult { Value = ok }, AppJsonContext.Default.BoolResult);
            });
        }

        private static DisplayBrightnessRequest ReadReq(HelperEnvelope env)
        {
            if (env.Payload is null) return new DisplayBrightnessRequest();
            return JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.DisplayBrightnessRequest)
                ?? new DisplayBrightnessRequest();
        }

        private static Task<HelperResult> Reply<T>(HelperEnvelope env, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            => Task.FromResult(new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(value, typeInfo),
            });
    }
}
#endif
