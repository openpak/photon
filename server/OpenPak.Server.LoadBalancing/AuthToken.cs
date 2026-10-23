using System.Text;
using OpenPak.Server.Common;

namespace OpenPak.Server.LoadBalancing;

/// <summary>A resolved identity recovered from an <see cref="AuthToken"/>: the userId (SteamID) and whether a
/// Steam ticket proved ownership. Anti-cheat/admin gating trusts the identity only when <see cref="Verified"/>.
/// <see cref="AppId"/> and <see cref="AppVersion"/> are what the client first authenticated with (params 224
/// and 220), or null for a token minted without them. <see cref="Region"/> is the Photon region the client
/// asked the Name Server for (param 210, "sa/*" stored as "sa"), or null when it sent none.</summary>
public readonly record struct AuthIdentity(string UserId, bool Verified, string? AppId = null, string? AppVersion = null,
                                           string? Region = null);

/// <summary>
/// An opaque token minted by the Name Server and validated by the Master/Game servers. The signed body is
/// "<c>userId|0</c>" (unverified) or "<c>userId|1</c>" (Steam-verified); the token is "<c>body.base64(signature)</c>",
/// the signature Ed25519 over the body (<see cref="TokenKeys"/>), so a region node can check a token with
/// home's public key and cannot make one. The client treats it as opaque (param 221).
///
/// The body may also carry the client's AppId and AppVersion and its chosen region:
/// "<c>userId|flag|appId|appVersion[|region]</c>", each URI-escaped so none can hold a '|'. A client sends
/// those only to the Name Server and re-authenticates on the Master and Game servers with this token alone
/// (seen on every live Outbound and Moving Out 2 session), so the token is how those roles learn which game
/// build a client is and which region it picked, as on Photon Cloud. User ids never contain '|'.
/// </summary>
public static class AuthToken
{
    private static string Sign(string body, TokenKeys keys)
        => $"{body}.{Convert.ToBase64String(keys.Sign(Encoding.UTF8.GetBytes(body)))}";

    /// <summary>Mints an UNVERIFIED token with no flag ("<c>userId.sig</c>"), which validates as unverified.</summary>
    public static string Mint(string userId, string secret) => Sign(userId, TokenKeys.FromSecret(secret));

    /// <summary>Mints with keys derived from a shared secret (home without a configured signing key; tests).</summary>
    public static string Mint(string userId, bool verified, string secret, string? appId = null, string? appVersion = null)
        => Mint(userId, verified, TokenKeys.FromSecret(secret), appId, appVersion);

    /// <summary>Mints a token carrying the signed verified flag (body "<c>userId|1</c>" / "<c>userId|0</c>").
    /// The flag is inside the signed body, so flipping it invalidates the token.</summary>
    /// <param name="appId">The AppId (param 224) the client authenticated with; with <paramref name="appVersion"/>,
    /// carried so the Master and Game servers keep its rooms with its own build's. Both null and no region =
    /// the two-field body.</param>
    /// <param name="region">The client's region token (param 210 without "/*"); null = none sent.</param>
    public static string Mint(string userId, bool verified, TokenKeys keys, string? appId = null, string? appVersion = null,
                              string? region = null)
    {
        var body = $"{userId}|{(verified ? "1" : "0")}";
        if (appId is not null || appVersion is not null || region is not null)
            body += $"|{Uri.EscapeDataString(appId ?? "")}|{Uri.EscapeDataString(appVersion ?? "")}";
        if (region is not null)
            body += $"|{Uri.EscapeDataString(region)}";
        return Sign(body, keys);
    }

    /// <summary>Validates with keys derived from a shared secret.</summary>
    public static Result<AuthIdentity> Validate(string token, string secret) => Validate(token, TokenKeys.FromSecret(secret));

    /// <summary>
    /// Validates a token and recovers its identity, distinguishing a malformed token
    /// (<see cref="ErrorCode.Corrupt"/>) from one whose signature doesn't verify
    /// (<see cref="ErrorCode.PermissionDenied"/>).
    /// </summary>
    public static Result<AuthIdentity> Validate(string token, TokenKeys keys)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return Result.Fail(ErrorCode.Corrupt);
        var body = token[..dot];
        byte[] sig;
        try { sig = Convert.FromBase64String(token[(dot + 1)..]); }
        catch (FormatException) { return Result.Fail(ErrorCode.Corrupt); }
        if (!keys.Verify(Encoding.UTF8.GetBytes(body), sig))
            return Result.Fail(ErrorCode.PermissionDenied);

        var fields = body.Split('|');
        if (fields.Length is 4 or 5)
            return new AuthIdentity(fields[0], fields[1] == "1",
                                    Uri.UnescapeDataString(fields[2]), Uri.UnescapeDataString(fields[3]),
                                    fields.Length == 5 && fields[4].Length > 0 ? Uri.UnescapeDataString(fields[4]) : null);
        var bar = body.LastIndexOf('|');
        return bar <= 0
            ? new AuthIdentity(body, false)                                   // no flag → unverified
            : new AuthIdentity(body[..bar], body[(bar + 1)..] == "1");
    }
}
