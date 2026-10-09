using System.Security.Cryptography;
using System.Text;
using OpenPak.Server.Common;

namespace OpenPak.Server.LoadBalancing;

/// <summary>A resolved identity recovered from an <see cref="AuthToken"/>: the userId (SteamID) and whether a
/// Steam ticket proved ownership. Anti-cheat/admin gating trusts the identity only when <see cref="Verified"/>.
/// <see cref="AppId"/> and <see cref="AppVersion"/> are what the client first authenticated with (params 224
/// and 220), or null for a token minted without them.</summary>
public readonly record struct AuthIdentity(string UserId, bool Verified, string? AppId = null, string? AppVersion = null);

/// <summary>
/// An opaque token minted by the Name Server and validated by the Master/Game servers. The signed body is
/// "<c>userId|0</c>" (unverified) or "<c>userId|1</c>" (Steam-verified); the token is "<c>body.base64HMAC(body)</c>".
/// A legacy "<c>userId.sig</c>" token (no flag) still validates, as unverified. The client treats it as opaque
/// (param 221).
///
/// The body may also carry the client's AppId and AppVersion: "<c>userId|flag|appId|appVersion</c>", each
/// URI-escaped so neither can hold a '|'. A client sends those two only to the Name Server and re-authenticates
/// on the Master and Game servers with this token alone (seen on every live Outbound and Moving Out 2 session),
/// so the token is how those roles learn which game build a client is — as on Photon Cloud. User ids never
/// contain '|'.
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
    /// <param name="appId">The AppId (param 224) the client authenticated with; with <paramref name="appVersion"/>,
    /// carried so the Master and Game servers keep its rooms with its own build's. Both null = the two-field body.</param>
    public static string Mint(string userId, bool verified, string secret, string? appId = null, string? appVersion = null)
    {
        var body = $"{userId}|{(verified ? "1" : "0")}";
        if (appId is not null || appVersion is not null)
            body += $"|{Uri.EscapeDataString(appId ?? "")}|{Uri.EscapeDataString(appVersion ?? "")}";
        return Sign(body, secret);
    }

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

        var fields = body.Split('|');
        if (fields.Length == 4)
            return new AuthIdentity(fields[0], fields[1] == "1",
                                    Uri.UnescapeDataString(fields[2]), Uri.UnescapeDataString(fields[3]));
        var bar = body.LastIndexOf('|');
        return bar <= 0
            ? new AuthIdentity(body, false)                                   // legacy token, no flag → unverified
            : new AuthIdentity(body[..bar], body[(bar + 1)..] == "1");
    }
}
