using System;
using System.Collections;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Per-peer in-room state stashed on PeerConnection.Tag once a peer joins a room.</summary>
public sealed record PeerRoomState(string RoomName, int Actor)
{
    /// <summary>Set once we've answered a Fusion Join Request with a Confirmation, cleared once the
    /// matching Start has been sent. A real Photon Cloud server sends Start only after the client's
    /// own NetworkConfigSync arrives (confirmed via a live capture) — sending it immediately after
    /// Confirmation instead raced the client into an immediate abort against our near-zero-latency
    /// local server. See GameServerHandler.HandleRaiseEvent.</summary>
    public FusionProtocol.JoinMessage? PendingFusionJoin { get; set; }
}

/// <summary>
/// Photon Game Server role: re-authenticates via token, enters the actual room (CreateGame/JoinGame),
/// assigns an actor number, and raises the Join event (255) — the in-room milestone.
/// </summary>
public sealed class GameServerHandler : IOperationHandler
{
    // Local aliases for the Photon codes this role uses; values come from PhotonCodes (single source of truth).
    private const byte OpAuthenticate = PhotonCodes.Op.Authenticate, OpCreateGame = PhotonCodes.Op.CreateGame, OpJoinGame = PhotonCodes.Op.JoinGame;
    private const byte OpRaiseEvent = PhotonCodes.Op.RaiseEvent, OpSetProperties = PhotonCodes.Op.SetProperties;
    private const byte PEventCode = PhotonCodes.Param.Code, PData = PhotonCodes.Param.Data;  // RaiseEvent: event code + data
    private const byte EvJoin = PhotonCodes.Event.Join;
    private const byte EvLeave = PhotonCodes.Event.Leave;
    private const byte EvPropertiesChanged = PhotonCodes.Event.PropertiesChanged;
    private const byte PRoomName = PhotonCodes.Param.RoomName, PSecret = PhotonCodes.Param.Secret,
                       PActorNr = PhotonCodes.Param.ActorNr, PActorList = PhotonCodes.Param.ActorList,
                       PGameProperties = PhotonCodes.Param.GameProperties, PActorProperties = PhotonCodes.Param.PlayerProperties,
                       PProperties = PhotonCodes.Param.Properties;
    private const byte PBroadcast = PhotonCodes.Param.Broadcast;
    private const byte PTargetActorNr = PhotonCodes.Param.TargetActorNr;  // EvPropertiesChanged: whose properties changed
    private const byte RoomMasterClientId = PhotonCodes.RoomProperty.MasterClientId;

    private readonly string _secret;
    private readonly RoomRegistry _registry;
    private readonly bool _allowAnonymousLan;
    private readonly AccountService? _accounts;
    private readonly RealmService? _realms;
    private readonly MotdService? _motd;
    private readonly ChatCommandHandler _chat;
    private readonly OperationRouter _router;
    private readonly IRoomLifecycleListener? _lifecycle;
    private readonly bool _trustLanAdmin;

    /// <param name="allowAnonymousLan">
    /// When true, tokenless auth (LAN mode) is accepted from loopback/private-range peers only.
    /// Defaults to false (secure): a valid token from the Master/Name Server is required.
    /// </param>
    /// <param name="accounts">Account store for ban enforcement on the resolved SteamID (optional in tests).</param>
    /// <param name="realms">Realm definitions whose ruleset is applied on join (optional in tests).</param>
    /// <param name="motd">Message of the Day service; when provided the resolved MOTD is stamped as a room property.</param>
    /// <param name="lifecycle">Optional sink fired on actor join/leave (the plugin manager); null = no plugins.</param>
    /// <param name="chatCommands">Commands runnable from in-game chat (the "/command" surface). Each command's
    /// MinLevel gates it against the caller's VERIFIED level; null → only built-in /motd works.</param>
    public GameServerHandler(string secret, RoomRegistry registry, bool allowAnonymousLan = false,
                             AccountService? accounts = null, RealmService? realms = null, MotdService? motd = null,
                             IRoomLifecycleListener? lifecycle = null, CommandRegistry? chatCommands = null,
                             bool trustLanAdmin = false)
    {
        _secret = secret;
        _registry = registry;
        _realms = realms;
        _allowAnonymousLan = allowAnonymousLan;
        _trustLanAdmin = trustLanAdmin;
        _accounts = accounts;
        _motd = motd;
        _lifecycle = lifecycle;
        _chat = new ChatCommandHandler(realms, motd, chatCommands);

        // OpRaiseEvent/OpSetProperties also still guard on PeerRoomState (peer.Tag) internally, so the
        // InRoom requirement here is detection-only telemetry, not the sole gate.
        _router = new OperationRouter("GameServer")
            .On(OpAuthenticate, HandleAuthenticate)
            .On(OpCreateGame, HandleEnterRoom, SessionStatus.Authenticated)
            .On(OpJoinGame, HandleEnterRoom, SessionStatus.Authenticated)
            .On(OpSetProperties, HandleSetProperties, SessionStatus.InRoom)
            .On(OpRaiseEvent, HandleRaiseEvent, SessionStatus.InRoom);
    }

    public void OnConnect(PeerConnection peer) { }

    public void OnDisconnect(PeerConnection peer)
    {
        if (peer.Tag is not PeerRoomState state) return;
        var session = _registry.Session(state.RoomName);
        // Notify remaining actors that this actor left (event 254, ActorNr = leaver).
        session.RelayFrom(state.Actor, new EventData(EvLeave, new() { { PActorNr, state.Actor } }));
        session.Leave(state.Actor);
        _lifecycle?.OnLeft(state.RoomName, state.Actor, session);   // plugins free per-actor state (e.g. team slot)
    }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request) => _router.Dispatch(peer, request);

    private void HandleAuthenticate(PeerConnection peer, OperationRequest request)
    {
        var response = Authenticate(request, _allowAnonymousLan && TrustedNetwork.IsLanOrLoopback(peer.Remote), peer);
        if (response.ReturnCode == 0) peer.Status = SessionStatus.Authenticated;
        peer.SendResponse(response);
    }

    private void HandleEnterRoom(PeerConnection peer, OperationRequest request)
    {
        // Reject a second join from a peer already in a room — otherwise it would orphan its previous
        // actor (still in the room's membership) while overwriting peer.Tag with the new one.
        if (peer.Tag is PeerRoomState already)
        {
            Log.Warn("GameServer", $"{peer.Remote} tried to join while already in \"{already.RoomName}\" -> rc=-7");
            peer.SendResponse(new OperationResponse(request.OperationCode, -7, "Already in a room", new()));
            return;
        }

        var (response, join) = EnterRoom(request, ExtractJoinPassword(request));
        peer.SendResponse(response);
        if (response.ReturnCode != 0) return;

        // Room name is client-supplied: take it only if it's actually a string (never coerce/NRE a null).
        var roomName = request.Parameters.TryGetValue(PRoomName, out var rn) && rn is string rns ? rns : "room";
        var actor = join.Parameters.TryGetValue(PActorNr, out var an) && an is int ai ? ai : 0;
        peer.Tag = new PeerRoomState(roomName, actor);
        peer.Status = SessionStatus.InRoom;
        var session = _registry.Session(roomName);
        session.Join(actor, peer);
        session.RelayFrom(actor, join);   // tell already-present actors this actor arrived (255)
        peer.RaiseEvent(join);             // and give the newcomer its own join
        session.ReplayCacheTo(actor);      // then replay cached spawns so it renders the existing world
        _lifecycle?.OnJoined(roomName, actor, session);   // plugins (e.g. game modes) react to the join
    }

    private void HandleSetProperties(PeerConnection peer, OperationRequest request)
    {
        var prs = peer.Tag as PeerRoomState;
        peer.SendResponse(SetProperties(prs?.RoomName, prs?.Actor, request));
    }

    /// <summary>The permission level to run a chat "/command" at. The account level is honored for a peer that
    /// is EITHER Steam-verified OR on a trusted local network when anonymous-LAN is enabled (the project's
    /// existing dev/operator trust boundary — no Steam validation runs on LAN, so requiring verification there
    /// would make in-game commands unusable for the operator). A public, unverified peer is always
    /// <c>Player</c>, regardless of the level its (spoofable) SteamID claims. Capped at <c>Admin</c>:
    /// <c>Console</c>-tier commands (e.g. loglevel/promote/ban) stay server-console-only even in chat.</summary>
    private PlayerLevel ChatLevelOf(PeerConnection peer)
    {
        // SECURE DEFAULT: only a Steam-VERIFIED identity is trusted for above-Player chat. The LAN-operator
        // opt-in (_trustLanAdmin) additionally trusts a trusted-local peer's account level — but the LAN
        // SteamID is asserted/spoofable, so it's off unless the operator enables it for a private LAN.
        bool trusted = peer.IsVerified
                       || (_trustLanAdmin && _allowAnonymousLan && TrustedNetwork.IsLanOrLoopback(peer.Remote));
        if (!trusted || peer.SteamId is null) return PlayerLevel.Player;
        var level = _accounts?.Find(peer.SteamId)?.Level ?? PlayerLevel.Player;
        return level > PlayerLevel.Admin ? PlayerLevel.Admin : level;   // Console-tier stays console-only
    }

    private void HandleRaiseEvent(PeerConnection peer, OperationRequest request)
    {
        var state = peer.Tag as PeerRoomState;
        var replies = _chat.TryHandle(state?.RoomName, request, ChatLevelOf(peer));
        if (replies is not null)
        {
            foreach (var reply in replies) peer.RaiseEvent(reply);   // server command handled (chunked); not relayed
            return;
        }
        if (state is not null
            && request.Parameters.TryGetValue(PEventCode, out var ecRaw) && ecRaw is byte ec
            && request.Parameters.TryGetValue(PData, out var data))
        {
            // Packet validation: a client must not raise the server-only lifecycle events (Join/Leave/
            // PropertiesChanged) — those are emitted by the server, and relaying a client-forged one would
            // let it spoof another player joining/leaving or changing properties. Drop the attempt.
            if (!IsClientRaisable(ec))
            {
                Log.Warn("GameServer", $"actor {state.Actor} in \"{state.RoomName}\" tried to raise reserved " +
                                       $"server event {PhotonNames.Event(ec)} -> dropped");
                return;
            }
            // Fusion's own session-negotiation protocol rides on this same event code (100, "protocol
            // package") — see FusionProtocol. A solo host's Join Request has no other real peer to
            // relay it to (that's the whole point: it's negotiating with whoever it's about to play
            // with), so without an answer the client waits ~30s and gives up. Answer its own Join
            // directly so a solo-host session can complete; still also relay below in case a second
            // real peer is present to negotiate with. Best-effort/reverse-engineered — see HANDOFF.md.
            if (ec == FusionProtocol.ProtocolPackageEventCode && data is byte[] payload)
            {
                if (FusionProtocol.TryDecodeJoin(payload) is { Type: FusionProtocol.JoinTypeRequest } join)
                {
                    var confirmation = FusionProtocol.EncodeJoinConfirmation(join, state.Actor);
                    peer.RaiseEvent(new EventData(ec, new() { { PData, confirmation } }));
                    // Defer Start until the client's own NetworkConfigSync arrives below — see
                    // PeerRoomState.PendingFusionJoin.
                    state.PendingFusionJoin = join;
                }
                else if (state.PendingFusionJoin is { } pending && FusionProtocol.PeekMessageId(payload) == FusionProtocol.MsgIdNetworkConfigSync)
                {
                    // GameMode came back Shared (2) on the wire (PluginGameMode), not ClientServer —
                    // Fusion's Shared-mode client only actually connects (and resolves
                    // StartGameOperation) when told to via the ConnectToShared flag; matches the
                    // user's own observation that a hosted session has no single fixed host and
                    // survives them leaving the screen.
                    var start = FusionProtocol.EncodeStart(pending.ProtocolVersion, pending.Major, pending.Minor, pending.Build,
                                                           remoteServerId: 0, FusionProtocol.StartRequestsConnectToShared);
                    peer.RaiseEvent(new EventData(ec, new() { { PData, start } }));
                    state.PendingFusionJoin = null;
                }
            }
            // Not a server command: relay this gameplay event to the other actors in the room.
            _registry.Session(state.RoomName).RelayFrom(state.Actor, new EventData(ec, new() { { PData, data } }), peer.CurrentInboundUnreliable);
        }
    }

    /// <summary>Event codes a client may legitimately raise. The server-emitted lifecycle events
    /// (Join/Leave/PropertiesChanged) are off-limits so a client can't forge them onto other actors.</summary>
    private static bool IsClientRaisable(byte eventCode) =>
        eventCode is not (PhotonCodes.Event.Join or PhotonCodes.Event.Leave or PhotonCodes.Event.PropertiesChanged);

    public OperationResponse Authenticate(OperationRequest r, bool allowAnonymous = false, PeerConnection? peer = null)
    {
        if (r.Parameters.TryGetValue(PSecret, out var t) && t is string token)
        {
            if (!AuthToken.Validate(token, _secret).TryGet(out var ident))
                return new OperationResponse(OpAuthenticate, -1, "Invalid token", new());
            if (_accounts?.Find(ident.UserId)?.IsBanned == true)
                return new OperationResponse(OpAuthenticate, -3, "Account banned", new());
            // Bind the identity the Name Server proved (or asserted) onto this connection. Anti-cheat/admin
            // gating trusts it only when Verified (a Steam ticket validated upstream).
            if (peer is not null) { peer.SteamId = ident.UserId; peer.IsVerified = ident.Verified; }
            return new OperationResponse(OpAuthenticate, 0, null, new());
        }

        return allowAnonymous
            ? new OperationResponse(OpAuthenticate, 0, null, new())
            : new OperationResponse(OpAuthenticate, -1, "Authentication token required", new());
    }

    public (OperationResponse Response, EventData Join) EnterRoom(OperationRequest r, string? joinPassword)
    {
        // Room name is client-supplied: accept only a real string, never coerce a wrong type or NRE a null.
        var name = r.Parameters.TryGetValue(PRoomName, out var n) && n is string ns ? ns : "room";
        var realm = _realms?.Get(name);
        Log.Debug("GameServer", $"EnterRoom name=\"{name}\" realm={(realm is null ? "<none>" : $"{realm.Name} enabled={realm.IsEnabled} pwd={(realm.Password.Length > 0)}")}");

        // A room name that matches a configured realm must be enabled (and password-gated below).
        // A room name that DOESN'T match any realm is a dynamic/ad-hoc room — e.g. a Fusion client's
        // own session code, created via Master's CreateGame/JoinGame before ever reaching here — and
        // is let through open, same as when no realms are configured at all (tests).
        if (realm is not null && !realm.IsEnabled)
        {
            Log.Warn("GameServer", $"EnterRoom rejected \"{name}\": realm disabled -> rc=-4");
            return (new OperationResponse(r.OperationCode, -4, "No such realm", new()), new EventData(EvJoin, new()));
        }
        if (realm is not null && realm.Password.Length > 0 && joinPassword != realm.Password)
        {
            Log.Warn("GameServer", $"EnterRoom rejected \"{name}\": wrong password -> rc=-5");
            return (new OperationResponse(r.OperationCode, -5, "Wrong password", new()), new EventData(EvJoin, new()));
        }

        var room = _registry.GetOrCreate(name);

        // Enforce the realm's capacity (MaxPlayers <= 0 means unlimited, so a misconfigured realm can't
        // lock everyone out). Without realms configured (tests) the room is open.
        if (realm is not null && realm.MaxPlayers > 0 && room.ActorNumbers.Count >= realm.MaxPlayers)
        {
            Log.Warn("GameServer", $"EnterRoom rejected \"{name}\": full ({room.ActorNumbers.Count}/{realm.MaxPlayers}) -> rc=-6");
            return (new OperationResponse(r.OperationCode, -6, "Room full", new()), new EventData(EvJoin, new()));
        }

        int actor = room.AddActor();
        Log.Info("GameServer", $"EnterRoom \"{name}\" actor={actor} occupants=[{string.Join(",", room.ActorNumbers)}]");

        // Stamp the realm ruleset into the room's game properties so the client sees it in-room.
        // MasterClientId is a well-known Photon room property (never previously populated here) that
        // every client reads to determine who's authoritative for the room; its absence looks to a
        // Fusion client like a room with no valid master, and it aborts the join (OpLeave) right after
        // entering. Convention (matches classic Photon): the lowest actor number currently in the room.
        var gameProps = new Dictionary<object, object> { { RoomMasterClientId, room.ActorNumbers.Min() } };
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

    /// <summary>Reads a join password from the request's GameProperties hashtable, if present.</summary>
    private static string? ExtractJoinPassword(OperationRequest r)
        => r.Parameters.TryGetValue(PGameProperties, out var gp)
           && gp is System.Collections.IDictionary d && d.Contains("Password")
            ? d["Password"]?.ToString() : null;
}
