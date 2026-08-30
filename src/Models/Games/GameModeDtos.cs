namespace Nexus.Service.Models.Games;

/// <summary>Pinned cross-repo contract with nexus-web (the top bar chip and
/// the settings section). Field names and shapes must not change on one side
/// alone.</summary>
public sealed class GameModeStatus
{
    /// <summary>"auto", "on" or "off" - the user's setting, not whether it is
    /// currently active.</summary>
    public string State { get; set; } = "auto";

    public bool Active { get; set; }

    /// <summary>"auto", "manual", or "" while inactive.</summary>
    public string Reason { get; set; } = "";

    public long ActivatedUtcMs { get; set; }

    public List<GameModeGame> Games { get; set; } = new();

    public GameModeEffectsDto Effects { get; set; } = new();
}

public sealed class GameModeGame
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public long SinceMs { get; set; }
}

/// <summary>What Game Mode is configured to do. Reported alongside the status
/// so the chip's tooltip does not need a second settings fetch.</summary>
public sealed class GameModeEffectsDto
{
    public bool HoldNotifications { get; set; }
    public bool HoldBackgroundNetwork { get; set; }
    public bool TurnPanelDisplaysOff { get; set; }
    public bool StopPanelRendering { get; set; }
    public int ExitGraceSeconds { get; set; }
}

public sealed class SetGameModeStateBody
{
    public string State { get; set; } = "auto";
}

public sealed class SetGameModeEffectsBody
{
    public bool? HoldNotifications { get; set; }
    public bool? HoldBackgroundNetwork { get; set; }
    public bool? TurnPanelDisplaysOff { get; set; }
    public bool? StopPanelRendering { get; set; }
    public int? ExitGraceSeconds { get; set; }
}

/// <summary>Revision frame for the "gameMode" multiplex topic; subscribers
/// refetch GET /api/game-mode on receive.</summary>
public sealed class GameModeChangedFrame
{
    public long Revision { get; set; }
}
