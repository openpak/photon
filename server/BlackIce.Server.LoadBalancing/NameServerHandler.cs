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
    // Local aliases for the Photon codes this role uses; values come from PhotonCodes (single source of truth).
    private const byte OpAuthenticate = PhotonCodes.Op.Authenticate;
    private const byte PAddress = PhotonCodes.Param.Address, PSecret = PhotonCodes.Param.Secret, PUserId = PhotonCodes.Param.UserId;

    private readonly string _masterAddress;
    private readonly string _secret;
    private readonly AccountService _accounts;
    private readonly OperationRouter _router;

    public NameServerHandler(string masterAddress, string secret, AccountService accounts)
    {
        _masterAddress = masterAddress;
        _secret = secret;
        _accounts = accounts;
        _router = new OperationRouter("NameServer", "Unsupported on Name Server")
            .On(OpAuthenticate, (peer, req) =>
            {
                var response = Authenticate(req);
                if (response.ReturnCode == 0) peer.Status = SessionStatus.Authenticated;
                peer.SendResponse(response);
            });
    }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

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
