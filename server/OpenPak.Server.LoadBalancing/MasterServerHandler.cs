using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Photon Master Server role: re-authenticates via the token, handles lobby + matchmaking, and
/// routes the client to the Game Server by returning its address (param 230) for CreateGame/JoinGame.
/// </summary>
public sealed class MasterServerHandler : IOperationHandler
{
    // Local aliases for the Photon codes this role uses; values come from PhotonCodes (single source of truth).
    private const byte OpAuthenticate = PhotonCodes.Op.Authenticate, OpJoinLobby = PhotonCodes.Op.JoinLobby,
                       OpCreateGame = PhotonCodes.Op.CreateGame, OpJoinGame = PhotonCodes.Op.JoinGame;
    private const byte EvGameList = PhotonCodes.Event.GameList;
    private const byte PAddress = PhotonCodes.Param.Address, PSecret = PhotonCodes.Param.Secret,
                       PRoomName = PhotonCodes.Param.RoomName, PGameListMap = PhotonCodes.Param.GameList;
    private const byte PLobbyName = PhotonCodes.Param.LobbyName;

    // Well-known room properties shown in the lobby room browser.
    private const byte RoomIsVisible = PhotonCodes.RoomProperty.IsVisible, RoomIsOpen = PhotonCodes.RoomProperty.IsOpen,
                       RoomPlayerCount = PhotonCodes.RoomProperty.PlayerCount, RoomMaxPlayers = PhotonCodes.RoomProperty.MaxPlayers;

    private readonly string _gameAddress;
    private readonly string _secret;
    private readonly RoomRegistry _registry;
    private readonly bool _allowAnonymousLan;
    private readonly AccountService? _accounts;
    private readonly RealmService? _realms;
    private readonly Func<string, int>? _lobbyBotCount;
    private readonly OperationRouter _router;

    /// <param name="allowAnonymousLan">
    /// When true, tokenless first-contact auth (the game's LAN mode) is accepted — but only from
    /// loopback/private-range peers. Defaults to false (secure): the full Name Server token is required.
    /// </param>
    /// <param name="accounts">Account store for ban enforcement on the resolved SteamID (optional in tests).</param>
    /// <param name="realms">Realm definitions advertised in the lobby browser (optional in tests).</param>
    /// <param name="lobbyBotCount">Optional per-room bot count added to a realm's advertised player count
    /// in the lobby browser. Null (the default) advertises only real players; wired from the bot manager
    /// when <c>Server.Bots.CountInLobby</c> is set, so operators choose whether bots look like players.</param>
    public MasterServerHandler(string gameAddress, string secret, RoomRegistry registry,
                               bool allowAnonymousLan = false, AccountService? accounts = null,
                               RealmService? realms = null, Func<string, int>? lobbyBotCount = null)
    {
        _gameAddress = gameAddress;
        _secret = secret;
        _registry = registry;
        _allowAnonymousLan = allowAnonymousLan;
        _accounts = accounts;
        _realms = realms;
        _lobbyBotCount = lobbyBotCount;

        _router = new OperationRouter("MasterServer")
            .On(OpAuthenticate, (peer, req) =>
            {
                bool anon = _allowAnonymousLan && TrustedNetwork.IsLanOrLoopback(peer.Remote);
                var response = Authenticate(req, anon, peer);
                if (response.ReturnCode == 0) peer.Status = SessionStatus.Authenticated;
                peer.SendResponse(response);
            })
            .On(OpJoinLobby, (peer, req) =>
            {
                peer.SendResponse(JoinLobby(req));
                // A client-named lobby (e.g. Fusion's session-code lobby, used to check whether a room
                // by that name/code already exists before hosting) has nothing to do with our own realm
                // browser — pushing the realm list there was read by the client as "a room already exists
                // under this code", which blocked it from ever proceeding to CreateGame. Only the default
                // (unnamed) lobby gets our realm list; a named lobby gets an empty one.
                bool isDefaultLobby = !req.Parameters.ContainsKey(PLobbyName);
                peer.RaiseEvent(isDefaultLobby ? BuildGameListEvent() : BuildFusionSessionLobbyEvent(req));
            }, SessionStatus.Authenticated)
            .On(OpCreateGame, (peer, req) => peer.SendResponse(CreateGame(req)), SessionStatus.Authenticated)
            .On(OpJoinGame, (peer, req) => peer.SendResponse(CreateGame(req)), SessionStatus.Authenticated);
    }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

    public OperationResponse Authenticate(OperationRequest r, bool allowAnonymous = false, PeerConnection? peer = null)
    {
        // Full Name Server flow: a token is present and must validate. Recover the identity (SteamID + whether
        // a Steam ticket proved it) and reject banned accounts.
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token)
        {
            if (!AuthToken.Validate(token, _secret).TryGet(out var ident))
                return new OperationResponse(OpAuthenticate, -1, "Invalid token", new());
            if (_accounts?.Find(ident.UserId)?.IsBanned == true)
                return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
            if (peer is not null) { peer.SteamId = ident.UserId; peer.IsVerified = ident.Verified; }
            return new OperationResponse(OpAuthenticate, 0, null, new());
        }

        // LAN/direct flow (UseNameServer=false): no token issued. Only honored when explicitly
        // enabled AND the peer is on a trusted local network (checked by the caller). Anonymous => never verified.
        if (!allowAnonymous)
            return new OperationResponse(OpAuthenticate, -1, "Authentication token required", new());

        var userId = Guid.NewGuid().ToString();
        if (peer is not null) { peer.SteamId = userId; peer.IsVerified = false; }
        return new OperationResponse(OpAuthenticate, 0, null, new()
        {
            { PSecret, AuthToken.Mint(userId, _secret) },   // hand back a token for the Game hop (unverified)
            { PhotonCodes.Param.UserId, userId },
        });
    }

    public OperationResponse JoinLobby(OperationRequest r) => new(OpJoinLobby, 0, null, new());

    /// <summary>
    /// Fusion (Outbound) session-code lobbies: the JOINER joins the lobby named after the host's
    /// room and waits for that session to appear in the GameList before it will OpJoinGame — an
    /// empty list left it waiting forever (observed live: joiner authenticated + JoinLobby, then
    /// only keepalives, "Invalid session ID" on the game screen). When a live room under the
    /// lobby's name exists, advertise it: the host's own stored GameProperties (scene/vehicleType/
    /// version/knownPlayers/MaxPlayers…) plus the well-known open/visible/count keys Fusion reads.
    /// </summary>
    private EventData BuildFusionSessionLobbyEvent(OperationRequest req)
    {
        var rooms = new Dictionary<string, object>();
        if (req.Parameters.TryGetValue(PLobbyName, out var lobbyObj) && lobbyObj is string lobbyName)
        {
            var room = _registry.Find(lobbyName);
            if (room is not null)
            {
                // Fusion's Photon Realtime RoomInfo parser unboxes the well-known numeric
                // properties as INT — byte-typed values (what EnterRoom stored) throw
                // InvalidCastException inside the client's event dispatch, the session list
                // silently stays empty, and the join times out into the generic error.
                // So: skip the byte-typed well-known keys from the host's stored props and
                // re-add them as proper types; pass everything else through untouched.
                // Fusion's Photon Realtime RoomInfo parser unboxes the well-known numeric
                // properties as INT — byte-typed VALUES (what EnterRoom stored) throw
                // InvalidCastException inside the client's event dispatch, the session list
                // silently stays empty, and the join times out into the generic error.
                // So: skip the byte-typed well-known keys from the host's stored props and
                // re-add them as proper types; pass everything else through untouched.
                var props = new Dictionary<object, object>
                {
                    { (object)RoomIsOpen, true },
                    { (object)RoomIsVisible, true },
                    { (object)RoomPlayerCount, room.ActorNumbers.Count },                    // int
                    { (object)RoomMaxPlayers, room.GameProperties.TryGetValue((object)RoomMaxPlayers, out var mp)
                        ? Convert.ToInt32(mp) : 4 },                                         // int
                };
                // Read the object-keyed game-property store (not the byte-keyed Properties dict): the
                // host's custom props (scene/vehicleType/knownPlayers/version) have STRING keys and only
                // exist here. Passing "version" et al. through is what lets Fusion match the session and
                // finally send OpJoinGame.
                foreach (var kv in room.GameProperties)
                {
                    if (kv.Key is byte b && (b == RoomPlayerCount || b == RoomMaxPlayers ||
                                             b == RoomIsOpen || b == RoomIsVisible ||
                                             b == 251 || b == 250 || b == 248 || b == 243))
                        continue;   // skip server-internal/custom-marker byte keys (250 = the
                                    // CustomProperties marker — merging a byte throws client-side;
                                    // 251 = RemovedFromList — Fusion's RoomInfo.InternalCacheProperties
                                    // (RVA 0x218cb30) reads it FIRST and drops the room if truthy, so a
                                    // stray stored 251 would hide the session from the joiner).
                    props[kv.Key] = kv.Value;
                }
                rooms[lobbyName] = props;
            }
        }
        return new EventData(EvGameList, new() { { PGameListMap, rooms } });
    }

    /// <summary>
    /// The lobby room list (event 230, param 222 = { roomName -> properties }), built from all
    /// enabled+visible realms. Each entry mixes well-known byte props with the custom string props
    /// the in-game room-browser slot hard-casts (PVP:bool, HackDifficultyIncrease:int, Password:string);
    /// omitting those makes the slot throw and the room silently fails to render.
    /// </summary>
    public EventData BuildGameListEvent()
    {
        var rooms = new Dictionary<string, object>();
        foreach (var realm in _realms?.ListVisible() ?? new List<Realm>())
        {
            // Real joined players, plus (only if the operator opted in) the live bots in that realm — so a
            // bot-stocked realm can look populated in the browser without inflating it when undesired.
            int players = (_registry.Find(realm.Name)?.ActorNumbers.Count ?? 0) + (_lobbyBotCount?.Invoke(realm.Name) ?? 0);
            // PUN's RoomInfo reads PlayerCount/MaxPlayers as a BYTE, so the wire can't represent >255: a raw
            // (byte) cast WRAPS (256 -> 0, corrupting the browser slot). Clamp instead, so a large or
            // uncapped realm shows a saturated "255" rather than a garbage count. (A single match that big
            // is a client-side limitation anyway — see docs/large-servers.md; the server itself scales by
            // running many realms, each advertised here.) MaxPlayers <= 0 means "unlimited" -> advertise 255.
            int advertisedMax = realm.MaxPlayers <= 0 ? byte.MaxValue : realm.MaxPlayers;
            rooms[realm.Name] = new Dictionary<object, object>
            {
                { RoomIsOpen, true },
                { RoomIsVisible, true },
                { RoomPlayerCount, ClampToByte(players) },
                { RoomMaxPlayers, ClampToByte(advertisedMax) },
                { "PVP", realm.Pvp },
                { "HackDifficultyIncrease", realm.HackDifficultyIncrease },
                { "Password", realm.Password },
            };
        }
        return new EventData(EvGameList, new() { { PGameListMap, rooms } });
    }

    /// <summary>Saturates a count to the 0..255 range PUN's byte-typed RoomInfo slots accept, so a value
    /// above the client's representable maximum shows as a full "255" rather than wrapping to garbage.</summary>
    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);

    public OperationResponse CreateGame(OperationRequest r)
    {
        // Room name is client-supplied: accept only a real string, never coerce a wrong type or NRE a null.
        var name = r.Parameters.TryGetValue(PRoomName, out var n) && n is string ns ? ns : $"Room-{Guid.NewGuid():N}";
        _registry.GetOrCreate(name);
        return new OperationResponse(r.OperationCode, 0, null, new()
        {
            { PAddress, _gameAddress },
            { PRoomName, name },
        });
    }
}
