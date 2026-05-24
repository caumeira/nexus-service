using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Obs;

public static class ObsProtocol
{
    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        var secret = Sha256Base64(password + salt);
        return Sha256Base64(secret + challenge);
    }

    private static string Sha256Base64(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes);
    }
}
