using System;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Store;

/// <summary>
/// Verifies the store's signature over an artifact's identity and hash. nexus-api
/// signs with the private half of <see cref="PublicKeySpki"/>; the message format
/// must match nexus-api src/store byte for byte.
/// </summary>
public static class StoreSignature
{
    /// <summary>P-256 SubjectPublicKeyInfo, DER, base64.</summary>
    public const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMHW7AXdEyEs168eMIqs7S0pSV0rUUosLizuW0oXrYXynLvVEGw7Jcpohisq3sD4c2ZDG/5ZMKVp/jmyxabMTbg==";

    public static byte[] Message(string appId, string version, string sha256) =>
        Encoding.UTF8.GetBytes($"nexus-app-signature-v1\n{appId}\n{version}\n{sha256.ToLowerInvariant()}");

    public static bool Verify(string appId, string version, string sha256, string? signature) =>
        Verify(appId, version, sha256, signature, PublicKeySpki);

    /// <summary>ECDSA P-256 / SHA-256, IEEE P1363 (r||s) signature in standard base64.</summary>
    internal static bool Verify(string appId, string version, string sha256, string? signature, string publicKeySpki)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            return key.VerifyData(
                Message(appId, version, sha256),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
