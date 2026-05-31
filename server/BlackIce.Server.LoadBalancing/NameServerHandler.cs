using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Photon Name Server role: authenticates the client, resolves/creates the account for the
/// SteamID it presents (Photon UserId, param 225), rejects bans, and hands the client to the
/// Master Server by returning the Master address (230), a token over the SteamID (221), and the
/// UserId (225).
/// </summary>
public sealed class NameServerHandler : IOperationHandler
{
    private const byte OpAuthenticate = 230;
    private const byte PAddress = 230, PSecret = 221, PUserId = 225;

    private readonly string _masterAddress;
    private readonly string _secret;
    private readonly AccountService _accounts;

    public NameServerHandler(string masterAddress, string secret, AccountService accounts)
    {
        _masterAddress = masterAddress;
        _secret = secret;
        _accounts = accounts;
    }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        if (request.OperationCode == OpAuthenticate)
        {
            peer.SendResponse(Authenticate(request));
            return;
        }
        Log.Warn("NameServer", $"{peer.Remote} unhandled {PhotonNames.Op(request.OperationCode)} " +
                               $"[{PhotonNames.Params(request.Parameters)}] -> rc=-2");
        peer.SendResponse(new OperationResponse(request.OperationCode, -2, "Unsupported on Name Server", new()));
    }

    public OperationResponse Authenticate(OperationRequest request)
    {
        // Identity = the SteamID the client sends as the Photon UserId (param 225). This is an
        // ASSERTED identity, not a proven one (any client can send any value) — see SECURITY.md.
        // We format-validate it as defense-in-depth (rejects junk/GUIDs); a malformed/absent value
        // falls back to a per-session non-authoritative id. Privilege escalation must NOT be gated
        // on this until Steam ticket validation is in place (tracked for SP3).
        var claimed = request.Parameters.TryGetValue(PUserId, out var u) && u is string s ? s : null;
        var steamId = SteamId.IsValidIndividual(claimed) ? claimed! : Guid.NewGuid().ToString();

        Log.Info("NameServer", $"authenticate: claimed UserId={(claimed is null ? "<none>" : $"\"{claimed}\"")} " +
                               $"-> steamId={steamId}{(SteamId.IsValidIndividual(claimed) ? "" : " (fallback guid)")}");
        var account = _accounts.ResolveOrCreate(steamId, steamId);
        if (account.IsBanned)
        {
            Log.Warn("NameServer", $"{steamId} is BANNED -> rc=-3");
            return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
        }

        return new OperationResponse(OpAuthenticate, 0, null, new()
        {
            { PAddress, _masterAddress },
            { PSecret, AuthToken.Mint(steamId, _secret) },
            { PUserId, steamId },
        });
    }
}
