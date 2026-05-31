using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing;

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
                var response = Authenticate(req, anon);
                if (response.ReturnCode == 0) peer.Status = SessionStatus.Authenticated;
                peer.SendResponse(response);
            })
            .On(OpJoinLobby, (peer, req) =>
            {
                peer.SendResponse(JoinLobby(req));
                peer.RaiseEvent(BuildGameListEvent());
            }, SessionStatus.Authenticated)
            .On(OpCreateGame, (peer, req) => peer.SendResponse(CreateGame(req)), SessionStatus.Authenticated)
            .On(OpJoinGame, (peer, req) => peer.SendResponse(CreateGame(req)), SessionStatus.Authenticated);
    }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

    public OperationResponse Authenticate(OperationRequest r, bool allowAnonymous = false)
    {
        // Full Name Server flow: a token is present and must validate. Recover the SteamID and
        // reject banned accounts.
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token)
        {
            if (!AuthToken.Validate(token, _secret).TryGet(out var steamId))
                return new OperationResponse(OpAuthenticate, -1, "Invalid token", new());
            if (_accounts?.Find(steamId)?.IsBanned == true)
                return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
            return new OperationResponse(OpAuthenticate, 0, null, new());
        }

        // LAN/direct flow (UseNameServer=false): no token issued. Only honored when explicitly
        // enabled AND the peer is on a trusted local network (checked by the caller).
        if (!allowAnonymous)
            return new OperationResponse(OpAuthenticate, -1, "Authentication token required", new());

        var userId = Guid.NewGuid().ToString();
        return new OperationResponse(OpAuthenticate, 0, null, new()
        {
            { PSecret, AuthToken.Mint(userId, _secret) },   // hand back a token for the Game hop
            { PhotonCodes.Param.UserId, userId },
        });
    }

    public OperationResponse JoinLobby(OperationRequest r) => new(OpJoinLobby, 0, null, new());

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
            rooms[realm.Name] = new Dictionary<object, object>
            {
                { RoomIsOpen, true },
                { RoomIsVisible, true },
                { RoomPlayerCount, (byte)players },
                { RoomMaxPlayers, (byte)realm.MaxPlayers },
                { "PVP", realm.Pvp },
                { "HackDifficultyIncrease", realm.HackDifficultyIncrease },
                { "Password", realm.Password },
            };
        }
        return new EventData(EvGameList, new() { { PGameListMap, rooms } });
    }

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
