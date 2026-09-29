using System;
using System.Security.Cryptography;
using Nexus.Service.Store;

namespace Nexus.Service.Tests.Store;

/// <summary>A per-run P-256 key standing in for nexus-api's store signing key.</summary>
internal static class StoreTestSigning
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static readonly string PublicKeySpki = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public static string Sign(string appId, string version, string sha256) =>
        Convert.ToBase64String(Key.SignData(
            StoreSignature.Message(appId, version, sha256),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
