using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

public class StoreSignatureTests
{
    // Produced by nexus-api's signArtifact (src/store/store-signing.service.ts) with its
    // test key, so this pins the message bytes and P1363 encoding across both sides.
    private const string ApiTestKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEpqzWe0KvSkVgl8wQ+8MqZ36jW5LbbCVJ/Q3nsCtck772Fwvqp2wQGm+ceb+e177QE1Pt603Ub0FR83bD2Bsegg==";
    private const string Sha = "abababababababababababababababababababababababababababababababab";
    private const string ApiSignature =
        "NqhUXiRpq1UdnLEFauFzVoeaG8YPuUbR+AnoPp6rp5zMvBgTA4GtiRI2vNXLh/AoR0qfLglw5tPdZ3FqaQWQ4g==";

    [Fact]
    public void VerifiesASignatureProducedByNexusApi() =>
        Assert.True(StoreSignature.Verify("com.x.app", "1.2.3", Sha, ApiSignature, ApiTestKeySpki));

    [Theory]
    [InlineData("com.x.other", "1.2.3")]
    [InlineData("com.x.app", "1.2.4")]
    public void RejectsTheSameSignatureForAnotherAppOrVersion(string appId, string version) =>
        Assert.False(StoreSignature.Verify(appId, version, Sha, ApiSignature, ApiTestKeySpki));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    public void RejectsMissingOrMalformedSignatures(string? signature) =>
        Assert.False(StoreSignature.Verify("com.x.app", "1.2.3", Sha, signature, ApiTestKeySpki));
}
