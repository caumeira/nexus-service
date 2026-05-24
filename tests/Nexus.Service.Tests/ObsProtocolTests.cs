using Nexus.Service.Obs;

namespace Nexus.Service.Tests;

public class ObsProtocolTests
{
    [Fact]
    public void ComputeAuthentication_UsesObsWebSocketV5ChallengeAlgorithm()
    {
        var auth = ObsProtocol.ComputeAuthentication("secret", "salt123", "challenge456");

        Assert.Equal("xgzgHJ5CaCNvrzkqxH6D2xMsV17ODXfIyB12Cj4aV1o=", auth);
    }
}
