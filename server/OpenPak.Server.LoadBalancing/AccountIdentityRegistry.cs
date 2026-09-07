using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenPak.Server.LoadBalancing;

public sealed class AccountIdentityRegistry
{
    private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);

    public string Register(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        var userId = $"openpak:{hash}";
        _identities[userId] = identity;
        return userId;
    }

    public string? Find(string userId) => _identities.TryGetValue(userId, out var identity) ? identity : null;

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
                var encoded = jwtParts[1].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                using var json = JsonDocument.Parse(Convert.FromBase64String(encoded));
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
