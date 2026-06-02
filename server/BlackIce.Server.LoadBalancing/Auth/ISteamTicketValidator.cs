using System.Threading;
using System.Threading.Tasks;

namespace BlackIce.Server.LoadBalancing.Auth;

/// <summary>The outcome of validating a client's Steam auth-session ticket.</summary>
public enum SteamAuthOutcome
{
    /// <summary>Steam confirmed the ticket; <see cref="SteamAuthResult.SteamId"/> is the proven owner.</summary>
    Verified,
    /// <summary>Steam rejected the ticket (forged, expired, banned, wrong app, etc.).</summary>
    Rejected,
    /// <summary>Validation could not run (no Steam runtime / not configured / timed out). Public peers fail closed.</summary>
    Unavailable,
}

/// <summary>Result of a ticket validation. <see cref="SteamId"/> is meaningful only when <see cref="Outcome"/>
/// is <see cref="SteamAuthOutcome.Verified"/>.</summary>
public readonly record struct SteamAuthResult(SteamAuthOutcome Outcome, ulong SteamId, string? Reason)
{
    public static SteamAuthResult Verified(ulong steamId) => new(SteamAuthOutcome.Verified, steamId, null);
    public static SteamAuthResult Rejected(string reason) => new(SteamAuthOutcome.Rejected, 0, reason);
    public static SteamAuthResult Unavailable(string reason) => new(SteamAuthOutcome.Unavailable, 0, reason);
}

/// <summary>
/// Validates a client's Steam game-server auth-session ticket, proving SteamID ownership so networked admin
/// and anti-cheat decisions can trust the identity. The real implementation (<c>BlackIce.Server.Steam</c>,
/// optional) calls <c>ISteamGameServer::BeginAuthSession</c> via Facepunch.Steamworks; the
/// <see cref="NullSteamTicketValidator"/> default (LAN/CI/no-Steam) returns
/// <see cref="SteamAuthOutcome.Unavailable"/>. Implementations must be safe to call from the listener thread
/// and complete asynchronously (Steam's verdict arrives via a callback).
/// </summary>
public interface ISteamTicketValidator
{
    Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct);
}
