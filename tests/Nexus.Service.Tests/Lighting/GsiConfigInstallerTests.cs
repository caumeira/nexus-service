using Nexus.Service.Lighting.GameSync;

namespace Nexus.Service.Tests.Lighting;

public class GsiConfigInstallerTests
{
    [Fact]
    public void BuildCfg_ContainsUri()
    {
        var cfg = GsiConfigInstaller.BuildCfg("abc123");
        Assert.Contains("http://127.0.0.1:9400/lighting/game-sync/gsi?token=abc123", cfg);
    }

    [Fact]
    public void BuildCfg_ContainsAuthToken()
    {
        var cfg = GsiConfigInstaller.BuildCfg("mytoken");
        Assert.Contains("\"token\"", cfg);
        Assert.Contains("mytoken", cfg);
    }

    [Fact]
    public void BuildCfg_ContainsDataKeys()
    {
        var cfg = GsiConfigInstaller.BuildCfg("t");
        Assert.Contains("\"provider\"", cfg);
        Assert.Contains("\"player_state\"", cfg);
        Assert.Contains("\"bomb\"", cfg);
        Assert.Contains("\"round\"", cfg);
        Assert.Contains("\"map\"", cfg);
        Assert.Contains("\"phase_countdowns\"", cfg);
    }

    [Fact]
    public void BuildCfg_ContainsAuthBlock()
    {
        var cfg = GsiConfigInstaller.BuildCfg("secret");
        Assert.Contains("\"auth\"", cfg);
    }

    [Fact]
    public void BuildCfg_TokenEmbeddedInBothUriAndAuthBlock()
    {
        var token = "unique_token_xyz";
        var cfg = GsiConfigInstaller.BuildCfg(token);
        // Token appears at least twice: once in the URI and once in the auth block.
        var count = 0;
        var idx = 0;
        while ((idx = cfg.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        Assert.True(count >= 2, $"Expected token to appear at least twice; found {count}");
    }
}
