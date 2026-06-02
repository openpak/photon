using System.Security.Cryptography;
using System.Text;
using BlackIce.Server.Common;

namespace BlackIce.Server.LoadBalancing;

/// <summary>A resolved identity recovered from an <see cref="AuthToken"/>: the userId (SteamID) and whether a
/// Steam ticket proved ownership. Anti-cheat/admin gating trusts the identity only when <see cref="Verified"/>.</summary>
public readonly record struct AuthIdentity(string UserId, bool Verified);

/// <summary>
/// An opaque token minted by the Name Server and validated by the Master/Game servers. The signed body is
/// "<c>userId|0</c>" (unverified) or "<c>userId|1</c>" (Steam-verified); the token is "<c>body.base64HMAC(body)</c>".
/// A legacy "<c>userId.sig</c>" token (no flag) still validates, as unverified. The client treats it as opaque
/// (param 221).
/// </summary>
public static class AuthToken
{
    private static string Sign(string body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"{body}.{Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(body)))}";
    }

    /// <summary>Mints an UNVERIFIED token. Back-compatible: the body has no flag, so it equals the old
    /// "<c>userId.sig</c>" format and validates as unverified.</summary>
    public static string Mint(string userId, string secret) => Sign(userId, secret);

    /// <summary>Mints a token carrying the signed verified flag (body "<c>userId|1</c>" / "<c>userId|0</c>").
    /// The flag is inside the HMAC body, so flipping it invalidates the token.</summary>
    public static string Mint(string userId, bool verified, string secret) =>
        Sign($"{userId}|{(verified ? "1" : "0")}", secret);

    /// <summary>
    /// Validates a token and recovers its identity, distinguishing a malformed token
    /// (<see cref="ErrorCode.Corrupt"/>) from one whose signature doesn't match the secret
    /// (<see cref="ErrorCode.PermissionDenied"/>).
    /// </summary>
    public static Result<AuthIdentity> Validate(string token, string secret)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return Result.Fail(ErrorCode.Corrupt);
        var body = token[..dot];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Sign(body, secret)), Encoding.UTF8.GetBytes(token)))
            return Result.Fail(ErrorCode.PermissionDenied);

        var bar = body.LastIndexOf('|');
        return bar <= 0
            ? new AuthIdentity(body, false)                                   // legacy token, no flag → unverified
            : new AuthIdentity(body[..bar], body[(bar + 1)..] == "1");
    }
}
