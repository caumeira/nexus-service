#if WINDOWS
using System;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Extracts the Authenticode signer certificate embedded in a PE file via
/// crypt32, without verifying the trust chain or revocation - callers that
/// need the full chain check (UpdateIntegrity, for staged installers) layer
/// WinVerifyTrust on top of this; callers that only need "is this signed and
/// by whom" (ProcessSignatureChecker, for live process detail) stop here.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AuthenticodeSigner
{
    /// <summary>Null when the file has no embedded Authenticode signature, or
    /// extraction failed for any other reason - the two are not
    /// distinguished, matching Win32's CryptQueryObject returning a plain
    /// false for both.</summary>
    public static unsafe System.Security.Cryptography.X509Certificates.X509Certificate2? TryGetSignerCertificate(string path)
    {
        // Extract the Authenticode signer cert from the file's embedded PKCS#7 via
        // crypt32, then materialize it with X509CertificateLoader.
        // (X509Certificate.CreateFromSignedFile is obsolete: SYSLIB0057.)
        var hStore = IntPtr.Zero;
        var hMsg = IntPtr.Zero;
        var pInfo = IntPtr.Zero;
        var pCert = IntPtr.Zero;
        try
        {
            if (!CryptQueryObject(CertQueryObjectFile, path,
                    CertQueryContentPkcs7SignedEmbed, CertQueryFormatBinary,
                    0, out _, out _, out _, out hStore, out hMsg, out _))
            {
                return null;
            }

            uint cb = 0;
            if (!CryptMsgGetParam(hMsg, CmsgSignerCertInfoParam, 0, IntPtr.Zero, ref cb) || cb == 0)
            {
                return null;
            }

            pInfo = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)cb);
            if (!CryptMsgGetParam(hMsg, CmsgSignerCertInfoParam, 0, pInfo, ref cb))
            {
                return null;
            }

            pCert = CertFindCertificateInStore(hStore, X509AsnEncoding | Pkcs7AsnEncoding,
                0, CertFindSubjectCert, pInfo, IntPtr.Zero);
            if (pCert == IntPtr.Zero)
            {
                return null;
            }

            var ctx = (CertContext*)pCert;
            var len = (int)ctx->cbCertEncoded;
            var raw = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(ctx->pbCertEncoded, raw, 0, len);
            return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(raw);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pCert != IntPtr.Zero) { CertFreeCertificateContext(pCert); }
            if (pInfo != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(pInfo); }
            if (hMsg != IntPtr.Zero) { CryptMsgClose(hMsg); }
            if (hStore != IntPtr.Zero) { CertCloseStore(hStore, 0); }
        }
    }

    // crypt32 interop for extracting the Authenticode signer certificate.
    private const uint CertQueryObjectFile = 0x1;
    private const uint CertQueryContentPkcs7SignedEmbed = 0x400;
    private const uint CertQueryFormatBinary = 0x2;
    private const uint CmsgSignerCertInfoParam = 7;
    private const uint X509AsnEncoding = 0x1;
    private const uint Pkcs7AsnEncoding = 0x10000;
    private const uint CertFindSubjectCert = 0xB0000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint dwCertEncodingType;
        public IntPtr pbCertEncoded;
        public uint cbCertEncoded;
        public IntPtr pCertInfo;
        public IntPtr hCertStore;
    }

    [System.Runtime.InteropServices.DllImport("crypt32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptQueryObject(
        uint dwObjectType,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pvObject,
        uint dwExpectedContentTypeFlags,
        uint dwExpectedFormatTypeFlags,
        uint dwFlags,
        out uint pdwMsgAndCertEncodingType,
        out uint pdwContentType,
        out uint pdwFormatType,
        out IntPtr phCertStore,
        out IntPtr phMsg,
        out IntPtr ppvContext);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptMsgGetParam(IntPtr hCryptMsg, uint dwParamType, uint dwIndex, IntPtr pvData, ref uint pcbData);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptMsgClose(IntPtr hCryptMsg);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern IntPtr CertFindCertificateInStore(IntPtr hCertStore, uint dwCertEncodingType, uint dwFindFlags, uint dwFindType, IntPtr pvFindPara, IntPtr pPrevCertContext);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertFreeCertificateContext(IntPtr pCertContext);

    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CertCloseStore(IntPtr hCertStore, uint dwFlags);
}
#endif
