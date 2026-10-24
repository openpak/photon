using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing.Auth;

namespace OpenPak.Server.LoadBalancing;

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
    private const byte OpGetRegions = PhotonCodes.Op.GetRegions;
    private const byte PAddress = PhotonCodes.Param.Address, PSecret = PhotonCodes.Param.Secret,
                       PUserId = PhotonCodes.Param.UserId, PTicket = PhotonCodes.Param.SteamTicket,
                       PRegion = PhotonCodes.Param.Region,
                       PAuthenticationParams = PhotonCodes.Param.ClientAuthenticationParams;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(3);

    // Home's own region, always listed first and always pointing at the home Master. A real Photon
    // region token: Outbound's "Region: Automatic" spun forever against a made-up "nx", consistent with
    // its auto-select only recognising Photon's own codes. Every other region is listed only under its
    // real Photon token too (RoutingTable aliases), and only while a node serves it.
    private const string RegionToken = "eu";

    private readonly string _masterAddress;
    private readonly string _loopbackMasterAddress;
    private readonly TokenKeys _keys;
    private readonly AccountService _accounts;
    private readonly ISteamTicketValidator _validator;
    private readonly Func<IPEndPoint, bool> _isLan;
    private readonly Action<Action> _post;
    private readonly AccountIdentityRegistry? _accountIdentities;
    private readonly NodeDirectory? _nodes;
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
                             Func<IPEndPoint, bool>? isLan = null, Action<Action>? post = null,
                             AccountIdentityRegistry? accountIdentities = null, NodeDirectory? nodes = null,
                             string? loopbackMasterAddress = null, TokenKeys? keys = null)
    {
        _masterAddress = masterAddress;
        // Loopback clients are handed a loopback master; the port comes from config (a modern
        // client's MasterServerAlt, e.g. 27001) rather than a constant. Defaults to the advertised one.
        _loopbackMasterAddress = loopbackMasterAddress ?? masterAddress;
        _keys = keys ?? TokenKeys.FromSecret(secret);
        _accounts = accounts;
        _validator = validator ?? new NullSteamTicketValidator();
        _allowAnonymousLan = allowAnonymousLan;
        _isLan = isLan ?? TrustedNetwork.IsLanOrLoopback;
        _post = post ?? (a => a());
        _accountIdentities = accountIdentities;
        _nodes = nodes;
        _router = new OperationRouter("NameServer", "Unsupported on Name Server")
            .On(OpAuthenticate, HandleAuthenticate)
            // AuthenticateOnce (the Photon C++ SDK's AuthOnce mode, Unrailed!) carries the same identity
            // params and expects the same answer, under its own op code; the Master then sees a token-only
            // Authenticate as usual. Its 193 (EncryptionMode 0 = payload encryption) needs nothing extra.
            .On(PhotonCodes.Op.AuthenticateOnce, HandleAuthenticate)
            .On(OpGetRegions, HandleGetRegions);
    }

    private void HandleGetRegions(PeerConnection peer, OperationRequest request) => peer.SendResponse(GetRegions(peer.Remote));

    /// <summary>The GetRegions answer for a client at <paramref name="remote"/>.</summary>
    public OperationResponse GetRegions(IPEndPoint remote)
    {
        // Hands off to the real Master Server address, same as Authenticate's PAddress below --
        // matches real Photon Cloud, which always names a genuinely different server/port here,
        // never the NameServer's own address. For LOOPBACK peers (local test clients) hand back
        // the loopback master so the client doesn't get routed to the real public Cloud.
        var master = TrustedNetwork.IsLoopback(remote) ? _loopbackMasterAddress : _masterAddress;
        var tokens = new List<string> { RegionToken };
        var addresses = new List<string> { master };
        // Each region with a node in rotation, under its real Photon token, with an address the client can
        // ping: with Region "Automatic" it pings them all and picks by its own latency. Test nodes only for
        // an address that recently authenticated as an allow-listed account (GetRegions carries no identity).
        // The Authenticate reply still names home's Master for every region: one global lobby.
        if (_nodes is not null)
            foreach (var (token, address) in _nodes.Adverts(_nodes.IsTesterAddress(remote.Address)))
            {
                tokens.Add(token);
                addresses.Add(address);
            }
        return new OperationResponse(OpGetRegions, 0, null, new()
        {
            { PRegion, tokens.ToArray() },
            { PAddress, addresses.ToArray() },
        });
    }

    /// <summary>The region a client asked for (param 210, "sa/*" or a cached best region "sa"), as a bare
    /// lower-case token, or null. Any value is accepted, never rejected: one we do not know resolves to home.</summary>
    internal static string? RegionOf(OperationRequest request)
    {
        if (!request.Parameters.TryGetValue(PRegion, out var r) || r is not string s) return null;
        var t = RoutingTable.NormalizeToken(s);
        return t.Length is > 0 and <= 16 && t.All(char.IsAsciiLetterOrDigit) ? t : null;
    }

    private readonly bool _allowAnonymousLan;

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

    private void HandleAuthenticate(PeerConnection peer, OperationRequest request)
    {
        var op = request.OperationCode;
        // The only hop where a client names its game build; Respond seals both into the token so the
        // Master and Game servers (which see only the token) keep its rooms with that build's.
        (peer.AppId, peer.AppVersion) = RoomRegistry.AppOf(request);
        peer.Region = RegionOf(request);
        var claimed = request.Parameters.TryGetValue(PUserId, out var u) && u is string s ? s : null;
        var validId = SteamId.IsValidIndividual(claimed) ? claimed! : null;

        // LAN/loopback (when allowed): legacy unverified path — accept the asserted id (or a throwaway guid).
        if (_allowAnonymousLan && _isLan(peer.Remote))
        {
            if (_accountIdentities is not null
                && request.Parameters.TryGetValue(PAuthenticationParams, out var rawParams)
                && rawParams is string authenticationParams
                && AccountIdentityRegistry.ExtractToken(authenticationParams) is { } bsDid)
            {
                var userId = _accountIdentities.Register(bsDid);
                if (_nodes is null)
                {
                    Respond(peer, userId, verified: false, op);
                    return;
                }
                // The Master needs the country by the time this peer creates a room; ask now.
                _ = _nodes.PrefetchCountryAsync(userId, bsDid, peer.Remote.Address);
                _ = RespondUnlessBanned(peer, userId, bsDid, _nodes, op);
                return;
            }
            Respond(peer, validId ?? Guid.NewGuid().ToString(), verified: false, op);
            return;
        }

        // Public peer: a Steam-validated ticket is REQUIRED (fail closed).
        // ...but first, refuse to grant a VERIFIED identity at all if the HMAC secret is the shipped default:
        // the verified flag is signed with it, so a default secret (public in the GPLv3 source) lets anyone
        // forge a verified token and present it straight to Master/Game, bypassing this validation entirely.
        // Fail closed for public until the operator sets a real secret. (Security review C1.)
        if (_keys.IsShippedDefault)
        {
            Log.Warn("NameServer", $"{peer.PeerTag} public auth refused: Server.Secret is the shipped default — " +
                                   "a verified identity would be forgeable. Set a strong Server.Secret.");
            peer.SendResponse(Fail(op, "Server is not configured for public authentication")); return;
        }
        if (validId is null)
        {
            Log.Warn("NameServer", $"{peer.PeerTag} public auth without a valid SteamID -> rc=-1");
            peer.SendResponse(Fail(op, "A valid SteamID is required")); return;
        }
        if (!(request.Parameters.TryGetValue(PTicket, out var tk) && tk is byte[] ticket && ticket.Length > 0))
        {
            Log.Warn("NameServer", $"{peer.PeerTag} public auth without a Steam ticket -> rc=-1");
            peer.SendResponse(Fail(op, "A Steam auth ticket is required")); return;
        }
        if (!ulong.TryParse(validId, out var sid64))
        {
            peer.SendResponse(Fail(op, "A valid SteamID is required")); return;
        }

        Log.Info("NameServer", $"{peer.PeerTag} validating Steam ticket for {Redact.Tag(validId)} ({ticket.Length}B)...");
        _ = ValidateAndRespond(peer, ticket, sid64, op);
    }

    private async Task ValidateAndRespond(PeerConnection peer, byte[] ticket, ulong assertedSteamId, byte op)
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
                Log.Info("NameServer", $"{peer.PeerTag} Steam VERIFIED as {Redact.Tag(result.SteamId)} -> rc=0");
                Respond(peer, result.SteamId.ToString(), verified: true, op);
            }
            else
            {
                Log.Warn("NameServer", $"{peer.PeerTag} Steam validation {result.Outcome} ({result.Reason}) -> rc=-1");
                peer.SendResponse(Fail(op, "Steam ticket validation failed"));
            }
        });
    }

    /// <summary>An OpenPak account banned in the admin is refused with the same rc=-3 "Account banned"
    /// as a locally banned one (website/docs/ban-lookup.md). The Switch adapter is the one that
    /// knows the account behind the identity.</summary>
    private async Task RespondUnlessBanned(PeerConnection peer, string userId, string identity, NodeDirectory nodes, byte op)
    {
        var banned = await nodes.IsBannedAsync(identity).ConfigureAwait(false);
        _post(() =>
        {
            if (banned)
            {
                Log.Warn("NameServer", $"{peer.PeerTag} OpenPak account BANNED -> rc=-3");
                peer.SendResponse(new OperationResponse(op, -3, "Account banned", new()));
                return;
            }
            Respond(peer, userId, verified: false, op);
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
        var (appId, appVersion) = RoomRegistry.AppOf(request);
        return BuildSuccess(steamId, verified: false, _loopbackMasterAddress, appId, appVersion, RegionOf(request));
    }

    /// <summary>Resolves the account, rejects bans, binds the identity onto the peer, and sends the success
    /// response (token carries the <paramref name="verified"/> flag).</summary>
    private void Respond(PeerConnection peer, string steamId, bool verified, byte op = OpAuthenticate)
    {
        var master = TrustedNetwork.IsLoopback(peer.Remote) ? _loopbackMasterAddress : _masterAddress;
        var resp = BuildSuccess(steamId, verified, master, peer.AppId, peer.AppVersion, peer.Region, op);
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
    private OperationResponse BuildSuccess(string steamId, bool verified, string masterAddress,
                                           string? appId = null, string? appVersion = null, string? region = null,
                                           byte op = OpAuthenticate)
    {
        var account = steamId.StartsWith("openpak:", StringComparison.Ordinal)
            ? null
            : _accounts.ResolveOrCreate(steamId, steamId);
        if (account?.IsBanned == true)
        {
            Log.Warn("NameServer", $"{Redact.Tag(steamId)} is BANNED -> rc=-3");
            return new OperationResponse(op, -3, "Account banned", new());
        }
        return new OperationResponse(op, 0, null, new()
        {
            { PAddress, masterAddress },
            { PSecret, AuthToken.Mint(steamId, verified, _keys, appId, appVersion, region) },
            { PUserId, steamId },
        });
    }

    private static OperationResponse Fail(byte op, string message) => new(op, -1, message, new());
}
