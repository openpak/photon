using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Auth;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Photon Name Server role: authenticates the client and hands it to the Master Server (address 230, a token
/// over the SteamID 221, the UserId 225).
///
/// <para><b>Identity trust model.</b> A LAN/loopback peer (when anonymous LAN is allowed) keeps the legacy
/// path: the asserted SteamID is accepted <em>unverified</em>. A public (non-LAN) peer MUST present a Steam
/// game-server auth ticket (param <see cref="PhotonCodes.Param.SteamTicket"/>), which is validated via
/// <see cref="ISteamTicketValidator"/> (BeginAuthSession). Only a Steam-verified SteamID is minted into the
/// token with the <c>verified</c> flag; a missing/invalid ticket fails closed (rc -1). Validation is async,
/// so the response is produced on a continuation marshalled back onto the listener thread via <c>post</c>.</para>
/// </summary>
public sealed class NameServerHandler : IOperationHandler
{
    private const byte OpAuthenticate = PhotonCodes.Op.Authenticate;
    private const byte PAddress = PhotonCodes.Param.Address, PSecret = PhotonCodes.Param.Secret,
                       PUserId = PhotonCodes.Param.UserId, PTicket = PhotonCodes.Param.SteamTicket;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(3);

    private readonly string _masterAddress;
    private readonly string _secret;
    private readonly AccountService _accounts;
    private readonly ISteamTicketValidator _validator;
    private readonly Func<IPEndPoint, bool> _isLan;
    private readonly Action<Action> _post;
    private readonly OperationRouter _router;

    /// <param name="validator">Steam ticket validator. Defaults to <see cref="NullSteamTicketValidator"/>
    /// (no Steam → public peers fail closed).</param>
    /// <param name="allowAnonymousLan">When true, LAN/loopback peers may authenticate without a ticket
    /// (unverified). Public peers always require a valid ticket.</param>
    /// <param name="isLan">Predicate identifying trusted-local peers; defaults to
    /// <see cref="TrustedNetwork.IsLanOrLoopback"/>.</param>
    /// <param name="post">Marshals a continuation onto the listener thread (the validation callback fires off-
    /// thread). Defaults to running inline (tests / no listener queue).</param>
    public NameServerHandler(string masterAddress, string secret, AccountService accounts,
                             ISteamTicketValidator? validator = null, bool allowAnonymousLan = true,
                             Func<IPEndPoint, bool>? isLan = null, Action<Action>? post = null)
    {
        _masterAddress = masterAddress;
        _secret = secret;
        _accounts = accounts;
        _validator = validator ?? new NullSteamTicketValidator();
        _allowAnonymousLan = allowAnonymousLan;
        _isLan = isLan ?? TrustedNetwork.IsLanOrLoopback;
        _post = post ?? (a => a());
        _router = new OperationRouter("NameServer", "Unsupported on Name Server")
            .On(OpAuthenticate, HandleAuthenticate);
    }

    private readonly bool _allowAnonymousLan;

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

    private void HandleAuthenticate(PeerConnection peer, OperationRequest request)
    {
        var claimed = request.Parameters.TryGetValue(PUserId, out var u) && u is string s ? s : null;
        var validId = SteamId.IsValidIndividual(claimed) ? claimed! : null;

        // LAN/loopback (when allowed): legacy unverified path — accept the asserted id (or a throwaway guid).
        if (_allowAnonymousLan && _isLan(peer.Remote))
        {
            Respond(peer, validId ?? Guid.NewGuid().ToString(), verified: false);
            return;
        }

        // Public peer: a Steam-validated ticket is REQUIRED (fail closed).
        if (validId is null)
        {
            Log.Warn("NameServer", $"{peer.Remote} public auth without a valid SteamID -> rc=-1");
            peer.SendResponse(Fail("A valid SteamID is required")); return;
        }
        if (!(request.Parameters.TryGetValue(PTicket, out var tk) && tk is byte[] ticket && ticket.Length > 0))
        {
            Log.Warn("NameServer", $"{peer.Remote} public auth without a Steam ticket -> rc=-1");
            peer.SendResponse(Fail("A Steam auth ticket is required")); return;
        }
        if (!ulong.TryParse(validId, out var sid64))
        {
            peer.SendResponse(Fail("A valid SteamID is required")); return;
        }

        Log.Info("NameServer", $"{peer.Remote} validating Steam ticket for {validId} ({ticket.Length}B)...");
        _ = ValidateAndRespond(peer, ticket, sid64);
    }

    private async Task ValidateAndRespond(PeerConnection peer, byte[] ticket, ulong assertedSteamId)
    {
        SteamAuthResult result;
        using var cts = new CancellationTokenSource(ValidationTimeout);
        try { result = await _validator.ValidateAsync(ticket, assertedSteamId, cts.Token); }
        catch (OperationCanceledException) { result = SteamAuthResult.Unavailable("validation timed out"); }
        catch (Exception ex) { result = SteamAuthResult.Unavailable($"validator threw: {ex.GetType().Name}"); }

        _post(() =>
        {
            if (result.Outcome == SteamAuthOutcome.Verified)
            {
                Log.Info("NameServer", $"{peer.Remote} Steam VERIFIED as {result.SteamId} -> rc=0");
                Respond(peer, result.SteamId.ToString(), verified: true);
            }
            else
            {
                Log.Warn("NameServer", $"{peer.Remote} Steam validation {result.Outcome} ({result.Reason}) -> rc=-1");
                peer.SendResponse(Fail("Steam ticket validation failed"));
            }
        });
    }

    /// <summary>
    /// Synchronous anonymous/asserted-identity authentication (the legacy LAN path), returning the response
    /// directly. The asserted SteamID is accepted UNVERIFIED (a malformed/absent value falls back to a
    /// throwaway id). Public callers go through the dispatch path (<see cref="HandleAuthenticate"/>) which
    /// requires a validated ticket; this method is the LAN/test entry point.
    /// </summary>
    public OperationResponse Authenticate(OperationRequest request)
    {
        var claimed = request.Parameters.TryGetValue(PUserId, out var u) && u is string s ? s : null;
        var steamId = SteamId.IsValidIndividual(claimed) ? claimed! : Guid.NewGuid().ToString();
        return BuildSuccess(steamId, verified: false);
    }

    /// <summary>Resolves the account, rejects bans, binds the identity onto the peer, and sends the success
    /// response (token carries the <paramref name="verified"/> flag).</summary>
    private void Respond(PeerConnection peer, string steamId, bool verified)
    {
        var resp = BuildSuccess(steamId, verified);
        if (resp.ReturnCode == 0)
        {
            peer.SteamId = steamId;
            peer.IsVerified = verified;
            peer.Status = SessionStatus.Authenticated;
        }
        peer.SendResponse(resp);
    }

    /// <summary>Resolves the account (rejecting bans) and builds the success response with a token carrying the
    /// <paramref name="verified"/> flag. No peer side effects — shared by the sync and async paths.</summary>
    private OperationResponse BuildSuccess(string steamId, bool verified)
    {
        var account = _accounts.ResolveOrCreate(steamId, steamId);
        if (account.IsBanned)
        {
            Log.Warn("NameServer", $"{steamId} is BANNED -> rc=-3");
            return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
        }
        return new OperationResponse(OpAuthenticate, 0, null, new()
        {
            { PAddress, _masterAddress },
            { PSecret, AuthToken.Mint(steamId, verified, _secret) },
            { PUserId, steamId },
        });
    }

    private static OperationResponse Fail(string message) => new(OpAuthenticate, -1, message, new());
}
