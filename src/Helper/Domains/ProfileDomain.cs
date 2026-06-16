#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains
{
    // Payload for profiles.list. Service-to-helper push on connect and on every
    // profile switch so the tray submenu stays current without polling.
    public sealed class ProfileListPayload
    {
        public List<ProfileEntry> Profiles { get; set; } = new();
        public string ActiveId { get; set; } = "";
    }

    // Payload for profiles.switch. Helper-to-service when the user picks a
    // profile from the tray submenu.
    public sealed class ProfileSwitchPayload
    {
        public string Id { get; set; } = "";
    }

    // JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

    [SupportedOSPlatform("windows")]
    public static class ProfileCommands
    {
        public static Task PushListAsync(HelperRegistry registry, List<ProfileEntry> profiles, string activeId, CancellationToken ct = default)
        {
            var conn = registry.GetAny();
            if (conn is null)
            {
                return Task.CompletedTask;
            }
            return conn.SendAsync(
                type: "profiles.list",
                payload: new ProfileListPayload { Profiles = profiles, ActiveId = activeId },
                payloadType: AppJsonContext.Default.ProfileListPayload,
                ct: ct);
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class ProfileListHandler
    {
        private readonly Action<ProfileListPayload> _onList;

        public ProfileListHandler(Action<ProfileListPayload> onList)
        {
            _onList = onList;
        }

        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register("profiles.list", (env, _) =>
            {
                if (env.Payload is null)
                {
                    return Task.FromResult(env.Ok());
                }
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ProfileListPayload);
                if (p is not null)
                {
                    _onList(p);
                }
                return Task.FromResult(env.Ok());
            });
        }
    }
}
#endif
