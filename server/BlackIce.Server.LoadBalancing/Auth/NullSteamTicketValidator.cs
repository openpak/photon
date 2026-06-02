using System.Threading;
using System.Threading.Tasks;

namespace BlackIce.Server.LoadBalancing.Auth;

/// <summary>
/// The default validator when no Steam runtime is configured (LAN/dev/CI builds, which stay Steam-free).
/// Always reports <see cref="SteamAuthOutcome.Unavailable"/>: public peers then fail closed (no trusted
/// identity), while the LAN/loopback anonymous path is unaffected. The real validator lives in the optional
/// <c>BlackIce.Server.Steam</c> project and is selected by the host only when present.
/// </summary>
public sealed class NullSteamTicketValidator : ISteamTicketValidator
{
    public Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct)
        => Task.FromResult(SteamAuthResult.Unavailable("Steam ticket validation is not configured on this server"));
}
