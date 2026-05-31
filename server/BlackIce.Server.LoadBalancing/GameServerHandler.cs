using System;
using System.Collections;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Per-peer in-room state stashed on PeerConnection.Tag once a peer joins a room.</summary>
public sealed record PeerRoomState(string RoomName, int Actor);

/// <summary>
/// Photon Game Server role: re-authenticates via token, enters the actual room (CreateGame/JoinGame),
/// assigns an actor number, and raises the Join event (255) — the in-room milestone.
/// </summary>
public sealed class GameServerHandler : IOperationHandler
{
    private const byte OpAuthenticate = 230, OpCreateGame = 227, OpJoinGame = 226;
    private const byte OpRaiseEvent = 253, OpSetProperties = 252;
    private const byte PEventCode = 244, PData = 245;     // RaiseEvent: event code + data/CustomData
    private const byte EvJoin = 255;
    private const byte EvLeave = 254;
    private const byte EvPropertiesChanged = 253;         // LoadBalancing EventCode.PropertiesChanged
    private const byte EvServerMessage = 199;             // our server->client message channel
    private const byte PunRpcEvent = 200;                 // PUN's RPC event code
    private const byte RpcMethodName = 3, RpcParams = 4;  // PUN RPC hashtable keys (method name / args)
    private const byte RpcMethodShortcut = 5;             // PUN sends a byte index here instead of the name
                                                          // when the method is in the project's RpcList
    private const byte PRoomName = 255, PSecret = 221, PActorNr = 254, PActorList = 252,
                       PGameProperties = 248, PActorProperties = 249, PProperties = 251;
    private const byte PBroadcast = 250;                  // ParameterCode.Broadcast on OpSetProperties
    private const byte PTargetActorNr = 253;              // EvPropertiesChanged: whose properties changed

    private readonly string _secret;
    private readonly RoomRegistry _registry;
    private readonly bool _allowAnonymousLan;
    private readonly AccountService? _accounts;
    private readonly RealmService? _realms;
    private readonly MotdService? _motd;

    /// <param name="allowAnonymousLan">
    /// When true, tokenless auth (LAN mode) is accepted from loopback/private-range peers only.
    /// Defaults to false (secure): a valid token from the Master/Name Server is required.
    /// </param>
    /// <param name="accounts">Account store for ban enforcement on the resolved SteamID (optional in tests).</param>
    /// <param name="realms">Realm definitions whose ruleset is applied on join (optional in tests).</param>
    /// <param name="motd">Message of the Day service; when provided the resolved MOTD is stamped as a room property.</param>
    public GameServerHandler(string secret, RoomRegistry registry, bool allowAnonymousLan = false,
                             AccountService? accounts = null, RealmService? realms = null, MotdService? motd = null)
    {
        _secret = secret;
        _registry = registry;
        _realms = realms;
        _allowAnonymousLan = allowAnonymousLan;
        _accounts = accounts;
        _motd = motd;
    }

    public void OnConnect(PeerConnection peer) { }

    public void OnDisconnect(PeerConnection peer)
    {
        if (peer.Tag is not PeerRoomState state) return;
        var session = _registry.Session(state.RoomName);
        // Notify remaining actors that this actor left (event 254, ActorNr = leaver).
        session.RelayFrom(state.Actor, new EventData(EvLeave, new() { { PActorNr, state.Actor } }));
        session.Leave(state.Actor);
    }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        switch (request.OperationCode)
        {
            case OpAuthenticate:
                peer.SendResponse(Authenticate(request, _allowAnonymousLan && TrustedNetwork.IsLanOrLoopback(peer.Remote)));
                break;
            case OpCreateGame:
            case OpJoinGame:
                var (response, join) = EnterRoom(request, ExtractJoinPassword(request));
                peer.SendResponse(response);
                if (response.ReturnCode == 0)
                {
                    var roomName = request.Parameters.TryGetValue(PRoomName, out var rn) ? rn.ToString()! : "room";
                    var actor = join.Parameters.TryGetValue(PActorNr, out var an) && an is int ai ? ai : 0;
                    peer.Tag = new PeerRoomState(roomName, actor);
                    var session = _registry.Session(roomName);
                    session.Join(actor, peer);
                    session.RelayFrom(actor, join);   // tell already-present actors this actor arrived (255)
                    peer.RaiseEvent(join);             // and give the newcomer its own join
                    session.ReplayCacheTo(actor);      // then replay cached spawns so it renders the existing world
                }
                break;
            case OpSetProperties:
                var prs = peer.Tag as PeerRoomState;
                peer.SendResponse(SetProperties(prs?.RoomName, prs?.Actor, request));
                break;
            case OpRaiseEvent:
                var state = peer.Tag as PeerRoomState;
                var reply = TryHandleChatCommand(state?.RoomName, request);
                if (reply is not null)
                {
                    peer.RaiseEvent(reply);   // server command handled; not relayed
                }
                else if (state is not null
                         && request.Parameters.TryGetValue(PEventCode, out var ecRaw) && ecRaw is byte ec
                         && request.Parameters.TryGetValue(PData, out var data))
                {
                    // Not a server command: relay this gameplay event to the other actors in the room.
                    _registry.Session(state.RoomName).RelayFrom(state.Actor, new EventData(ec, new() { { PData, data } }), peer.CurrentInboundUnreliable);
                }
                break;
            default:
                // Any in-room op we don't implement yet. NOTE: a -2 here is NOT harmless — a live
                // capture showed the client abandons the room (back to main menu) when an in-room op
                // it expects (e.g. OpSetProperties, now handled above) is rejected. The Warn log
                // names exactly which op the game still needs so we can implement it.
                Log.Warn("GameServer", $"{peer.Remote} unhandled {PhotonNames.Op(request.OperationCode)} " +
                                       $"[{PhotonNames.Params(request.Parameters)}] -> rc=-2");
                peer.SendResponse(new OperationResponse(request.OperationCode, -2, "Unknown operation", new()));
                break;
        }
    }

    public OperationResponse Authenticate(OperationRequest r, bool allowAnonymous = false)
    {
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token)
        {
            if (!AuthToken.TryValidate(token, _secret, out var steamId))
                return new OperationResponse(OpAuthenticate, -1, "Invalid token", new());
            if (_accounts?.Find(steamId)?.IsBanned == true)
                return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
            return new OperationResponse(OpAuthenticate, 0, null, new());
        }

        return allowAnonymous
            ? new OperationResponse(OpAuthenticate, 0, null, new())
            : new OperationResponse(OpAuthenticate, -1, "Authentication token required", new());
    }

    public (OperationResponse Response, EventData Join) EnterRoom(OperationRequest r, string? joinPassword)
    {
        var name = r.Parameters.TryGetValue(PRoomName, out var n) ? n.ToString()! : "room";
        var realm = _realms?.Get(name);
        Log.Debug("GameServer", $"EnterRoom name=\"{name}\" realm={(realm is null ? "<none>" : $"{realm.Name} enabled={realm.IsEnabled} pwd={(realm.Password.Length > 0)}")}");

        // When realms are configured, only known+enabled realms are joinable, and a locked
        // realm requires the matching password. (No realms configured = open, for tests.)
        if (_realms is not null && (realm is null || !realm.IsEnabled))
        {
            Log.Warn("GameServer", $"EnterRoom rejected \"{name}\": no such realm / disabled -> rc=-4");
            return (new OperationResponse(r.OperationCode, -4, "No such realm", new()), new EventData(EvJoin, new()));
        }
        if (realm is not null && realm.Password.Length > 0 && joinPassword != realm.Password)
        {
            Log.Warn("GameServer", $"EnterRoom rejected \"{name}\": wrong password -> rc=-5");
            return (new OperationResponse(r.OperationCode, -5, "Wrong password", new()), new EventData(EvJoin, new()));
        }

        var room = _registry.GetOrCreate(name);
        int actor = room.AddActor();
        Log.Info("GameServer", $"EnterRoom \"{name}\" actor={actor} occupants=[{string.Join(",", room.ActorNumbers)}]");

        // Stamp the realm ruleset into the room's game properties so the client sees it in-room.
        var gameProps = new Dictionary<object, object>();
        if (realm is not null)
        {
            gameProps["PVP"] = realm.Pvp;
            gameProps["HackDifficultyIncrease"] = realm.HackDifficultyIncrease;
            gameProps["Password"] = realm.Password;
        }

        var motdText = _motd?.Resolve(realm);
        if (!string.IsNullOrWhiteSpace(motdText)) gameProps["motd"] = motdText!;
        Log.Debug("GameServer", $"EnterRoom gameProps={PhotonNames.Value(gameProps)} motd={(motdText is null ? "<none>" : $"\"{motdText}\"")}");

        var response = new OperationResponse(r.OperationCode, 0, null, new()
        {
            { PActorNr, actor },
            { PGameProperties, gameProps },
            { PActorProperties, new Dictionary<byte, object>() },
        });
        var join = new EventData(EvJoin, new()
        {
            { PActorNr, actor },
            { PActorList, room.ActorNumbers.ToArray() },
        });
        return (response, join);
    }

    /// <summary>
    /// Handles OpSetProperties (252): the client sets its player properties (ActorNr present) or the
    /// shared game properties, optionally broadcasting the change. We persist the values in the room,
    /// acknowledge success, and — when Broadcast(250) is set — relay an EvPropertiesChanged (253) event
    /// to the OTHER actors so they learn the change (e.g. a player's appearance/gear).
    ///
    /// Rejecting this op (the previous default-case behavior) made the PUN client treat the in-room
    /// property set as failed and abort back to the main menu — accepting it is required to stay
    /// in-game. Without the broadcast relay, remote clients never received the appearance properties
    /// and rendered each other with default/missing gear.
    ///
    /// Wire shape of the relayed event matches what the LoadBalancing client reads in its event 253
    /// handler: key TargetActorNr(253) = the actor whose properties changed (0/absent for shared game
    /// properties), key Properties(251) = the changed hashtable. The change is relayed reliably:
    /// appearance is not a position stream and must not be dropped.
    /// </summary>
    public OperationResponse SetProperties(string? roomName, int? senderActor, OperationRequest req)
    {
        var room = roomName is not null ? _registry.Find(roomName) : null;
        if (room is not null && req.Parameters.TryGetValue(PProperties, out var p) && p is IDictionary props)
        {
            // Player properties carry an explicit ActorNr; absent means the shared game properties.
            int? actorNr = req.Parameters.TryGetValue(PActorNr, out var a) && a is int ai ? ai : null;
            room.SetProperties(actorNr, props);

            var broadcast = req.Parameters.TryGetValue(PBroadcast, out var b) && b is bool flag && flag;
            if (broadcast && senderActor is int sender)
            {
                // TargetActorNr identifies whose props these are: the named actor for player props,
                // or 0 for shared game props (the client reads 0 as "game properties").
                var target = actorNr ?? 0;
                var ev = new EventData(EvPropertiesChanged, new()
                {
                    { PProperties, props },
                    { PTargetActorNr, target },
                });
                _registry.Session(roomName!).RelayFrom(sender, ev);   // reliable: appearance must not drop
            }
        }
        else
        {
            Log.Warn("GameServer", $"SetProperties: unresolved room (\"{roomName}\") or missing Properties(251)");
        }
        return new OperationResponse(OpSetProperties, 0, null, new());
    }

    /// <summary>Builds a ServerMessage event: a server->client text line the BlackIce.Motd plugin renders.</summary>
    public static EventData ServerMessageEvent(string text) =>
        new(EvServerMessage, new() { { PData, text } });

    /// <summary>
    /// Inspects an inbound RaiseEvent (op 253). If it is a chat RPC whose text is a "/command",
    /// returns the ServerMessage to send back and the caller must NOT relay it. Returns null for
    /// non-commands (normal chat / other events) so future relay logic handles them.
    ///
    /// PUN serializes the RPC method either as the string name (key 3) or, when the method is in
    /// the project's RpcList, as a byte shortcut index (key 5). The shortcut index lives in the
    /// game's PhotonServerSettings asset, not in code, so we cannot resolve it statically. We
    /// therefore only ever intercept "/"-prefixed text (which only player chat carries): a named
    /// "ReceiveChatMessage" RPC, or a shortcut RPC (no name, key 5 present) whose first arg is a
    /// "/command". This works regardless of the shortcut index and leaves all normal RPCs and
    /// normal chat untouched. (B5 live capture should confirm no other RPC sends "/"-leading text.)
    /// </summary>
    public EventData? TryHandleChatCommand(string? roomName, OperationRequest req)
    {
        if (req.OperationCode != OpRaiseEvent) return null;
        // Event code comes off the wire from an untrusted peer; validate rather than coerce.
        // A real PUN client always sends it as a GpBinary byte, so anything else (wrong type
        // or out of byte range) is malformed and ignored — no exception on bad input.
        if (!req.Parameters.TryGetValue(PEventCode, out var ec) || ec is not byte ecByte || ecByte != PunRpcEvent) return null;
        if (!req.Parameters.TryGetValue(PData, out var d) || d is not IDictionary rpc) return null;

        var method = rpc.Contains(RpcMethodName) ? rpc[RpcMethodName] as string : null;
        var isShortcutRpc = rpc.Contains(RpcMethodShortcut);
        var args = rpc.Contains(RpcParams) ? rpc[RpcParams] as object[] : null;
        var text = (args is { Length: > 0 } ? args[0] as string : null)?.Trim();

        if (string.IsNullOrEmpty(text) || text![0] != '/') return null;          // only intercept /commands
        var isChat = method == "ReceiveChatMessage" || (method is null && isShortcutRpc);
        if (!isChat) return null;

        Log.Info("GameServer", $"chat command intercepted: \"{text}\" (room=\"{roomName}\", " +
                               $"method={(method ?? "<shortcut>")})");
        if (text.Equals("/motd", StringComparison.OrdinalIgnoreCase))
        {
            var realm = roomName is not null ? _realms?.Get(roomName) : null;
            var resolved = _motd?.Resolve(realm);
            return ServerMessageEvent(string.IsNullOrWhiteSpace(resolved) ? "No MOTD set." : resolved!);
        }
        return ServerMessageEvent($"Unknown command: {text}");
    }

    /// <summary>Reads a join password from the request's GameProperties hashtable, if present.</summary>
    private static string? ExtractJoinPassword(OperationRequest r)
        => r.Parameters.TryGetValue(PGameProperties, out var gp)
           && gp is System.Collections.IDictionary d && d.Contains("Password")
            ? d["Password"]?.ToString() : null;
}
