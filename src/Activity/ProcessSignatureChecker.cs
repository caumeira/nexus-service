namespace Nexus.Service.Activity;

/// <summary>
/// Classifies whether an executable carries an embedded Authenticode
/// signature, without verifying the trust chain or revocation - a live
/// process detail endpoint needs "is this signed, and by whom", not full
/// chain validation (see UpdateIntegrity for that, applied to staged
/// installers). Authenticode is a Windows PE concept; every other platform
/// always reports "unknown".
/// </summary>
public static class ProcessSignatureChecker
{
    public static (string Signed, string? Publisher) Check(string exePath)
    {
#if WINDOWS
        using var cert = Nexus.Service.Platform.Windows.AuthenticodeSigner.TryGetSignerCertificate(exePath);
        if (cert is null)
        {
            return ("unsigned", null);
        }

        var publisher = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
        return ("signed", string.IsNullOrEmpty(publisher) ? null : publisher);
#else
        return ("unknown", null);
#endif
    }
}
