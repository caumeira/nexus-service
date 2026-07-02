using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;
using Xunit;

namespace Nexus.Service.Tests;

// Guards that the SM2 implementation stays wire-compatible with the sm-crypto library
// the Tryx cloud server uses. CtNode was produced by sm-crypto's doEncrypt(msg, pub, 1)
// (C1C3C2, C1 with no 04 prefix) for private key d=1; a passing decrypt proves our layout,
// KDF, and C3 match the reference exactly. Verified both directions against sm-crypto.
public class Sm2CrossCheckTests
{
    private const string Msg = "hello-nexus-sm2";
    private const string Priv = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string CtNode = "400377bbfd7d318ef9c73700a7055045ac3750db1f3c7a0ade4771f037a078f1a09f287be909382893be7d7aff47fe3f1f043d560bc40f14106521f7b16f56e79e3ccf443e3b17b7d4ebcd225ea70b1997047f5710da42b73ba31f214cb157d11300bf686532b393ad8bb9acd989d6";

    [Fact]
    public void CSharp_decrypts_smcrypto_ciphertext()
    {
        Assert.Equal(Msg, Sm2.Decrypt(CtNode, Priv));
    }
}
