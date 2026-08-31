using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Games;
using Nexus.Service.Models.Games;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Game Mode status and controls. The state field is the user's setting
/// ("auto" / "on" / "off"); `active` is what it currently resolves to, which
/// also depends on whether a tracked game is running.
/// </summary>
public static class GameModeRoutes
{
    private const int MinExitGraceSeconds = 0;
    private const int MaxExitGraceSeconds = 600;

    public static void MapGameModeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/game-mode", (GameModeState state, IConfigStore config) =>
            BuildStatus(state, config));

        app.MapPost("/api/game-mode", (SetGameModeStateBody body, GameModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            state.SetManualState(body.State);
            PanelTopics.BroadcastGameMode(hub);
            return BuildStatus(state, config);
        });

        app.MapPost("/api/game-mode/effects", (SetGameModeEffectsBody body, GameModeState state, IConfigStore config, MultiplexHub hub) =>
        {
            config.Update(s =>
            {
                s.GameMode ??= new GameModeSettings();
                if (body.HoldNotifications is bool holdNotifications)
                    s.GameMode.HoldNotifications = holdNotifications;
                if (body.HoldBackgroundNetwork is bool holdNetwork)
                    s.GameMode.HoldBackgroundNetwork = holdNetwork;
                if (body.TurnPanelDisplaysOff is bool displaysOff)
                    s.GameMode.TurnPanelDisplaysOff = displaysOff;
                if (body.ExitGraceSeconds is int grace)
                    s.GameMode.ExitGraceSeconds = Math.Clamp(grace, MinExitGraceSeconds, MaxExitGraceSeconds);
            });

            // An effect toggled while Game Mode is already active has to take
            // hold now, not at the next activation.
            state.ReapplyEffects();
            PanelTopics.BroadcastGameMode(hub);
            return BuildStatus(state, config);
        });
    }

    private static GameModeStatus BuildStatus(GameModeState state, IConfigStore config)
    {
        var settings = LoadSettings(config);
        return new GameModeStatus
        {
            State = GameModeState.Normalize(settings.State),
            Active = state.IsActive,
            Reason = state.Reason,
            ActivatedUtcMs = state.ActivatedUtcMs,
            Games = state.Games
                .Select(g => new GameModeGame { Key = g.GameKey, Name = g.Name, Pid = g.Pid, SinceMs = g.StartedUtcMs })
                .ToList(),
            Effects = new GameModeEffectsDto
            {
                HoldNotifications = settings.HoldNotifications,
                HoldBackgroundNetwork = settings.HoldBackgroundNetwork,
                TurnPanelDisplaysOff = settings.TurnPanelDisplaysOff,
                ExitGraceSeconds = settings.ExitGraceSeconds,
            },
        };
    }

    private static GameModeSettings LoadSettings(IConfigStore config)
    {
        try { return config.Load().GameMode ?? new GameModeSettings(); }
        catch { return new GameModeSettings(); }
    }
}
