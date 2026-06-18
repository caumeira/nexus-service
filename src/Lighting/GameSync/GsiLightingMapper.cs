using System;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Lighting.GameSync;

public static class GsiLightingMapper
{
    public static (byte r, byte g, byte b) MapToColor(Cs2GsiPayload p)
    {
        var player = p.Player;
        var state = player?.State;
        var round = p.Round;
        var bomb = p.Bomb;

        if (player is null || state is null || player.Activity == "menu")
        {
            return (8, 8, 8);
        }

        if (state.Flashed > 30)
        {
            var v = (byte)state.Flashed;
            return (v, v, v);
        }

        if (state.Burning > 0)
        {
            return (255, 90, 0);
        }

        var bombState = bomb?.State;
        var roundBomb = round?.Bomb;

        if (bombState == "defused")
        {
            return (0, 255, 0);
        }

        if (bombState == "exploded")
        {
            return (255, 0, 0);
        }

        if (bombState == "planted" || bombState == "defusing" || roundBomb == "planted")
        {
            // CS2 sends remaining seconds as a decimal string (e.g. "34.5"). Default 40 when absent.
            double countdown = 40.0;
            if (bomb?.Countdown is { } cdStr &&
                double.TryParse(cdStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                countdown = parsed;
            }

            // Red brightens as the bomb timer runs down.
            var t = 1.0 - Math.Clamp(countdown / 40.0, 0.0, 1.0);
            var brightness = (byte)(120 + (int)(t * 135));
            return (brightness, 0, 0);
        }

        if (round?.Phase == "over")
        {
            if (!string.IsNullOrEmpty(round.WinTeam) && round.WinTeam == player.Team)
            {
                return (0, 200, 0);
            }

            return (200, 0, 0);
        }

        if (round?.Phase == "freezetime")
        {
            return (0, 80, 255);
        }

        // Live: team base color lerped toward red as health drops.
        (byte br, byte bg, byte bb) = player.Team switch
        {
            "CT" => ((byte)0, (byte)120, (byte)255),
            "T"  => ((byte)255, (byte)180, (byte)0),
            _    => ((byte)0, (byte)200, (byte)0),
        };

        var factor = Math.Clamp(state.Health, 0, 100) / 100.0;
        var r = (byte)(255 + (int)((br - 255) * factor));
        var g = (byte)(0   + (int)((bg - 0)   * factor));
        var b = (byte)(0   + (int)((bb - 0)   * factor));
        return (r, g, b);
    }
}
