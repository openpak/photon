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
    private const byte OpAuthenticate = 230, OpJoinLobby = 229, OpCreateGame = 227, OpJoinGame = 226;
    private const byte EvGameList = 230;
    private const byte PAddress = 230, PSecret = 221, PRoomName = 255, PGameListMap = 222;

    // Well-known room properties shown in the lobby room browser.
    private const byte RoomIsVisible = 254, RoomIsOpen = 253, RoomPlayerCount = 252, RoomMaxPlayers = 255;

    private readonly string _gameAddress;
    private readonly string _secret;
    private readonly RoomRegistry _registry;
    private readonly bool _allowAnonymousLan;
    private readonly AccountService? _accounts;
    private readonly RealmService? _realms;

    /// <param name="allowAnonymousLan">
    /// When true, tokenless first-contact auth (the game's LAN mode) is accepted — but only from
    /// loopback/private-range peers. Defaults to false (secure): the full Name Server token is required.
    /// </param>
    /// <param name="accounts">Account store for ban enforcement on the resolved SteamID (optional in tests).</param>
    /// <param name="realms">Realm definitions advertised in the lobby browser (optional in tests).</param>
    public MasterServerHandler(string gameAddress, string secret, RoomRegistry registry,
                               bool allowAnonymousLan = false, AccountService? accounts = null,
                               RealmService? realms = null)
    {
        _gameAddress = gameAddress;
        _secret = secret;
        _registry = registry;
        _allowAnonymousLan = allowAnonymousLan;
        _accounts = accounts;
        _realms = realms;
    }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        switch (request.OperationCode)
        {
            case OpAuthenticate:
                bool anon = _allowAnonymousLan && TrustedNetwork.IsLanOrLoopback(peer.Remote);
                peer.SendResponse(Authenticate(request, anon));
                break;
            case OpJoinLobby:
                peer.SendResponse(JoinLobby(request));
                peer.RaiseEvent(BuildGameListEvent());
                break;
            case OpCreateGame:
            case OpJoinGame:
                peer.SendResponse(CreateGame(request));
                break;
            default:
                Log.Warn("MasterServer", $"{peer.Remote} unhandled {PhotonNames.Op(request.OperationCode)} " +
                                         $"[{PhotonNames.Params(request.Parameters)}] -> rc=-2");
                peer.SendResponse(new OperationResponse(request.OperationCode, -2, "Unknown operation", new()));
                break;
        }
    }

    public OperationResponse Authenticate(OperationRequest r, bool allowAnonymous = false)
    {
        // Full Name Server flow: a token is present and must validate. Recover the SteamID and
        // reject banned accounts.
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token)
        {
            if (!AuthToken.TryValidate(token, _secret, out var steamId))
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
            { 225, userId },                                 // ParameterCode.UserId
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
            int players = _registry.Find(realm.Name)?.ActorNumbers.Count ?? 0;
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
        var name = r.Parameters.TryGetValue(PRoomName, out var n) ? n.ToString()! : $"Room-{Guid.NewGuid():N}";
        _registry.GetOrCreate(name);
        return new OperationResponse(r.OperationCode, 0, null, new()
        {
            { PAddress, _gameAddress },
            { PRoomName, name },
        });
    }
}
