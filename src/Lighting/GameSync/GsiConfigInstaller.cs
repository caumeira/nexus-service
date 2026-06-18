using System.IO;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.GameSync;

public static class GsiConfigInstaller
{
    private const string Cs2AppId = "730";
    private const string CfgFileName = "gamestate_integration_nexus.cfg";

    public static void EnsureInstalled(string token)
    {
        var installDir = SteamLibraryLocator.FindAppInstallDir(Cs2AppId);
        if (installDir is null)
        {
            ServiceLog.Info("[gsi] CS2 not found; cfg not written");
            return;
        }

        var cfgDir = Path.Combine(installDir, "game", "csgo", "cfg");
        if (!Directory.Exists(cfgDir))
        {
            ServiceLog.Info("[gsi] CS2 cfg dir not found; cfg not written");
            return;
        }

        var cfgPath = Path.Combine(cfgDir, CfgFileName);
        var content = BuildCfg(token);
        File.WriteAllText(cfgPath, content);
        ServiceLog.Info("[gsi] CS2 cfg written");
    }

    public static void RemoveIfPresent()
    {
        var installDir = SteamLibraryLocator.FindAppInstallDir(Cs2AppId);
        if (installDir is null)
        {
            return;
        }

        var cfgPath = Path.Combine(installDir, "game", "csgo", "cfg", CfgFileName);
        if (File.Exists(cfgPath))
        {
            File.Delete(cfgPath);
            ServiceLog.Info("[gsi] CS2 cfg removed");
        }
    }

    // Returns the cfg file text. Pure; no I/O.
    internal static string BuildCfg(string token)
    {
        return
            "\"nexus Game State Configuration\"\n" +
            "{\n" +
            $"    \"uri\"           \"http://127.0.0.1:9400/lighting/game-sync/gsi?token={token}\"\n" +
            "    \"timeout\"       \"1.1\"\n" +
            "    \"buffer\"        \"0.1\"\n" +
            "    \"throttle\"      \"0.1\"\n" +
            "    \"heartbeat\"     \"10.0\"\n" +
            "    \"auth\"\n" +
            "    {\n" +
            $"        \"token\"     \"{token}\"\n" +
            "    }\n" +
            "    \"data\"\n" +
            "    {\n" +
            "        \"provider\"              \"1\"\n" +
            "        \"map\"                   \"1\"\n" +
            "        \"round\"                 \"1\"\n" +
            "        \"player_id\"             \"1\"\n" +
            "        \"player_state\"          \"1\"\n" +
            "        \"player_match_stats\"    \"1\"\n" +
            "        \"bomb\"                  \"1\"\n" +
            "        \"phase_countdowns\"      \"1\"\n" +
            "    }\n" +
            "}\n";
    }
}
