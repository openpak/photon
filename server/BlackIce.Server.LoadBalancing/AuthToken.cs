using System.Security.Cryptography;
using System.Text;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// An opaque token minted by the Name Server and validated by the Master/Game servers.
/// Format: "<userId>.<base64 HMAC-SHA256(userId)>". The client treats it as opaque (param 221).
/// </summary>
public static class AuthToken
{
    public static string Mint(string userId, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(userId)));
        return $"{userId}.{sig}";
    }

    public static bool TryValidate(string token, string secret, out string userId)
    {
        userId = "";
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;
        var id = token[..dot];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Mint(id, secret)), Encoding.UTF8.GetBytes(token)))
            return false;
        userId = id;
        return true;
    }
}
