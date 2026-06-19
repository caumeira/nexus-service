using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Update;

/// <summary>
/// Integrity checks for a staged installer. SHA-256 is always enforced.
/// Authenticode chain + signer-thumbprint verification is built but gated
/// behind a compile-time constant; flip it to true when the Nexus signing
/// certificate is live.
/// </summary>
public static class UpdateIntegrity
{
    // SHA-256 is always enforced. Authenticode chain verification is built
    // but disabled until a Nexus code-signing certificate is deployed.
    // Trust boundary: SHA-256 from SHA256SUMS (author-published) detects
    // tampering of the staged file; it does not prove the release author's
    // identity. Authenticode will provide that when AuthenticodeEnforced = true.
    private const bool AuthenticodeEnforced = false;

#if WINDOWS
    /// <summary>Thumbprint allowlist for the Nexus signing certificate.</summary>
    private static readonly string[] AllowedThumbprints = Array.Empty<string>();
#endif

    private const int BufferSize = 81920;

    /// <summary>
    /// Verifies the staged installer at <paramref name="path"/>. Always checks
    /// SHA-256. When <see cref="AuthenticodeEnforced"/> is true (Windows only),
    /// also verifies the Authenticode chain and signer thumbprint.
    /// Throws <see cref="InvalidDataException"/> on any failure.
    /// </summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha256))
        {
            throw new InvalidDataException("Cannot verify: SHA-256 is required.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Staged installer not found at {path}.");
        }

        var actualSha = await ComputeSha256Async(path, ct);
        if (!string.Equals(actualSha, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch on {Path.GetFileName(path)}: got {actualSha}, expected {expectedSha256.ToLowerInvariant()}.");
        }

#if WINDOWS
        if (AuthenticodeEnforced)
        {
            VerifyAuthenticode(path);
        }
        else
        {
            // Log Authenticode state without blocking when not enforced.
            try
            {
                var thumbprint = GetSignerThumbprint(path);
                Console.Error.WriteLine($"[update-integrity] Authenticode thumbprint: {thumbprint ?? "(not signed)"}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update-integrity] Authenticode read failed (not enforced): {ex.Message}");
            }
        }
#endif
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyAuthenticode(string path)
    {
        var thumbprint = GetSignerThumbprint(path);
        if (thumbprint is null)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not Authenticode-signed.");
        }

        var matched = false;
        foreach (var allowed in AllowedThumbprints)
        {
            if (string.Equals(thumbprint, allowed, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            throw new InvalidDataException(
                $"Authenticode thumbprint {thumbprint} is not in the Nexus allowlist.");
        }

        VerifyTrustChain(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? GetSignerThumbprint(string path)
    {
        try
        {
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
            using var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
            return x509.Thumbprint;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyTrustChain(string path)
    {
        // WinVerifyTrust P/Invoke: verifies the full chain + revocation.
        var actionGuid = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        unsafe
        {
            var fileInfo = new WinVerifyTrustFileInfo();
            fixed (char* p = path)
            {
                fileInfo.cbStruct = (uint)sizeof(WinVerifyTrustFileInfo);
                fileInfo.pcwszFilePath = p;
                fileInfo.hFile = IntPtr.Zero;
                fileInfo.pgKnownSubject = IntPtr.Zero;

                var trustData = new WinTrustData();
                trustData.cbStruct = (uint)sizeof(WinTrustData);
                trustData.dwUIChoice = 2; // WTD_UI_NONE
                trustData.fdwRevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
                trustData.dwUnionChoice = 1; // WTD_CHOICE_FILE
                trustData.pFile = &fileInfo;
                trustData.dwStateAction = 0; // WTD_STATEACTION_IGNORE

                var result = WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);
                if (result != 0)
                {
                    throw new InvalidDataException($"WinVerifyTrust returned 0x{result:X8}.");
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WinTrustData pWVTData);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private unsafe struct WinVerifyTrustFileInfo
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WinVerifyTrustFileInfo* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
#endif
}
