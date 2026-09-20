using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenPak.Server.LoadBalancing;

public sealed class AccountIdentityRegistry
{
    // Both Name listeners (:5058 and the alt port) run their own loop on their own thread and share
    // this registry, so registration is genuinely concurrent.
    private readonly ConcurrentDictionary<string, string> _identities = new(StringComparer.Ordinal);

    /// <summary>
    /// Mints the peer's public Photon UserId and remembers the credential it came from. The UserId is
    /// derived from <see cref="StableKey"/>, NOT from the credential itself: it is what the game keys a
    /// player's persistent identity on, so it must survive a re-login (see the remarks there).
    /// </summary>
    public string Register(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StableKey(identity))))[..32];
        var userId = $"openpak:{hash}";
        _identities[userId] = identity;
        return userId;
    }

    public string? Find(string userId) => _identities.TryGetValue(userId, out var identity) ? identity : null;

    /// <summary>
    /// The part of an identity that survives a re-login, and so is safe to hash into a UserId.
    /// <para>
    /// A <c>nex:</c> identity is an account-server-signed credential minted per session, whose payload is
    /// <c>&lt;pid&gt;.&lt;username&gt;.&lt;expiry&gt;</c> — only the pid names the account. Hashing the whole
    /// credential (as this did until 2026-09-20) handed a returning player a brand-new UserId on every
    /// login, so a host's world could no longer match them to the character it had stored. A
    /// <c>bsdid:</c> identity is a console's stable device id already.
    /// </para>
    /// Unparseable input falls back to the identity verbatim: worse (it rotates), never wrong.
    /// </summary>
    internal static string StableKey(string identity)
    {
        if (!identity.StartsWith("nex:nx2.", StringComparison.Ordinal)) return identity;
        if (identity["nex:".Length..].Split('.') is not { Length: 3 } nx2) return identity;
        if (DecodeBase64Url(nx2[1]) is not { } payload) return identity;

        var claims = Encoding.UTF8.GetString(payload);
        var dot = claims.IndexOf('.');
        return dot > 0 ? $"nex:pid:{claims[..dot]}" : identity;
    }

    /// <summary>Decodes unpadded base64url (JWT segments, nx2 payloads); null when it is not valid base64.</summary>
    private static byte[]? DecodeBase64Url(string segment)
    {
        var encoded = segment.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        var buffer = new byte[encoded.Length];
        return Convert.TryFromBase64String(encoded, buffer, out var written) ? buffer[..written] : null;
    }

    public static string? ExtractToken(string authenticationParams)
    {
        foreach (var part in authenticationParams.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var value = Uri.UnescapeDataString(equals >= 0 ? part[(equals + 1)..] : part);
            var jwtParts = value.Split('.');
            if (jwtParts.Length != 3) continue;
            try
            {
                if (DecodeBase64Url(jwtParts[1]) is not { } claims) continue;
                using var json = JsonDocument.Parse(claims);
                // Emulator tokens carry an account-server-signed nx2 credential. Prefer it over
                // bs:did, which emulators generate randomly and therefore cannot identify an account.
                if (json.RootElement.TryGetProperty("openpak", out var nexClaim)
                    && nexClaim.GetString() is { Length: > 0 } nexToken)
                    return $"nex:{nexToken}";
                if (json.RootElement.TryGetProperty("bs:did", out var deviceClaim)
                    && deviceClaim.GetString() is { Length: > 0 } bsDid)
                    return $"bsdid:{bsDid}";
            }
            catch (Exception) { }
        }
        return null;
    }
}
