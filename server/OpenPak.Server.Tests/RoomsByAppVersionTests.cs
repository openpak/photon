using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// Photon Cloud keeps every (AppId, AppVersion) pair in its own world: two builds of one game never
/// see or join each other's rooms, even under the same room name. A client names its build only to the
/// Name Server and re-authenticates on the Master and Game servers with the token alone (as every live
/// Outbound and Moving Out 2 session does), so the token carries the pair to both. A client that sends
/// neither keeps the behaviour from before versions counted.
/// </summary>
public class RoomsByAppVersionTests
{
    private const string Secret = "s";
    private const string WeWereHere = "6f95ce9e-3a91-40ce-b11e-49c0864676a9";
    private const string Outbound = "cffbe809-5036-43d8-84a1-7bf16c924721";

    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private sealed class Client
    {
        public PeerConnection Peer { get; } =
            new("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        public List<OperationResponse> Responses { get; } = new();
        public List<EventData> Events { get; } = new();

        public Client()
        {
            Peer.OnResponse = Responses.Add;
            Peer.OnRaised = Events.Add;
        }
    }

    private static Dictionary<byte, object> AuthParams(string? appId, string? appVersion)
    {
        var p = new Dictionary<byte, object>();
        if (appId is not null) p[PhotonCodes.Param.ApplicationId] = appId;
        if (appVersion is not null) p[PhotonCodes.Param.AppVersion] = appVersion;
        return p;
    }

    /// <summary>The server's three roles over one registry, as the host wires them.</summary>
    private sealed class Server
    {
        public RoomRegistry Registry { get; } = new();
        public NameServerHandler Name { get; } = new("127.0.0.1:5055", Secret, TestAccounts.Create());
        public MasterServerHandler Master { get; }
        public GameServerHandler Game { get; }

        public Server(RealmService? realms = null)
        {
            Master = new MasterServerHandler("127.0.0.1:5056", Secret, Registry, realms: realms);
            Game = new GameServerHandler(Secret, Registry, realms: realms);
        }

        /// <summary>The Name Server hop, the only one where the client names its build.</summary>
        private string Token(string? appId, string? appVersion)
        {
            var response = Name.Authenticate(new OperationRequest(PhotonCodes.Op.Authenticate, AuthParams(appId, appVersion)));
            Assert.Equal(0, response.ReturnCode);
            return (string)response.Parameters[PhotonCodes.Param.Secret];
        }

        private static Client SignIn(IOperationHandler role, string token)
        {
            var c = new Client();
            role.OnOperationRequest(c.Peer, new OperationRequest(PhotonCodes.Op.Authenticate, new() { { PhotonCodes.Param.Secret, token } }));
            Assert.Equal(0, c.Responses[^1].ReturnCode);
            return c;
        }

        public Client MasterClient(string? appId, string? appVersion) => SignIn(Master, Token(appId, appVersion));

        public Client GameClient(string? appId, string? appVersion) => SignIn(Game, Token(appId, appVersion));

        /// <summary>Master CreateGame then the Game-server join, the way a Fusion host enters a session.</summary>
        public int Host(string? appId, string? appVersion, string room)
        {
            var m = MasterClient(appId, appVersion);
            Master.OnOperationRequest(m.Peer, new OperationRequest(PhotonCodes.Op.CreateGame, new() { { PhotonCodes.Param.RoomName, room } }));
            Assert.Equal(room, m.Responses[^1].Parameters[PhotonCodes.Param.RoomName]);   // the client sees its own name
            return Join(appId, appVersion, room);
        }

        public int Join(string? appId, string? appVersion, string room)
        {
            var g = GameClient(appId, appVersion);
            Game.OnOperationRequest(g.Peer, new OperationRequest(PhotonCodes.Op.JoinGame, new() { { PhotonCodes.Param.RoomName, room } }));
            Assert.Equal(0, g.Responses[^1].ReturnCode);
            return (int)g.Responses[^1].Parameters[PhotonCodes.Param.ActorNr];
        }

        /// <summary>The rooms a client of this build sees in the lobby named after a session code.</summary>
        public Dictionary<string, object> SessionLobby(string? appId, string? appVersion, string lobby)
        {
            var m = MasterClient(appId, appVersion);
            Master.OnOperationRequest(m.Peer, new OperationRequest(PhotonCodes.Op.JoinLobby, new() { { PhotonCodes.Param.LobbyName, lobby } }));
            var list = Assert.Single(m.Events, e => e.Code == PhotonCodes.Event.GameList);
            return (Dictionary<string, object>)list.Parameters[PhotonCodes.Param.GameList];
        }
    }

    [Fact]
    public void SameNameDifferentAppVersionIsTwoRooms()
    {
        var server = new Server();
        Assert.Equal(1, server.Host(WeWereHere, "1.3.0-prod", "Q35V9E"));
        // An older build asking for the same code gets a room of its own, as its first actor.
        Assert.Equal(1, server.Join(WeWereHere, "1.2.0-prod", "Q35V9E"));

        Assert.Equal(2, server.Registry.All.Count);
        Assert.All(server.Registry.All, r => Assert.Equal("Q35V9E", r.Name));   // the public name is untouched
        Assert.All(server.Registry.All, r => Assert.Single(r.ActorNumbers));
    }

    [Fact]
    public void AnotherBuildsRoomIsNotListed()
    {
        var server = new Server();
        server.Host(WeWereHere, "1.3.0-prod", "Q35V9E");

        Assert.Empty(server.SessionLobby(WeWereHere, "1.2.0-prod", "Q35V9E"));
        Assert.Empty(server.SessionLobby(Outbound, "1.3.0-prod", "Q35V9E"));   // same version, other game
        Assert.Contains("Q35V9E", server.SessionLobby(WeWereHere, "1.3.0-prod", "Q35V9E").Keys);
    }

    [Fact]
    public void SameAppIdAndVersionJoinsAsBefore()
    {
        var server = new Server();
        Assert.Equal(1, server.Host(Outbound, "1.1.7", "R2"));
        Assert.Equal(2, server.Join(Outbound, "1.1.7", "R2"));

        var room = Assert.Single(server.Registry.All);
        Assert.Equal("R2", room.Name);
        Assert.Equal(new[] { 1, 2 }, room.ActorNumbers);
    }

    [Fact]
    public void ClientsWithoutAppIdOrVersionKeepTheBareName()
    {
        var server = new Server();
        Assert.Equal(1, server.Host(null, null, "R3"));
        Assert.Equal(2, server.Join(null, null, "R3"));

        var room = Assert.Single(server.Registry.All);
        Assert.Equal("R3", room.Key);   // exactly the registry key it had before versions counted
        Assert.Contains("R3", server.SessionLobby(null, null, "R3").Keys);
        // A versioned build does not land in it either.
        Assert.Equal(1, server.Join(Outbound, "1.1.7", "R3"));
    }

    [Fact]
    public void RealmListCountsOnlyTheAskingBuildsPlayers()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true, IsVisible = true });
        db.Context.SaveChanges();
        var server = new Server(new RealmService(db.Context));
        server.Join(Outbound, "1.1.7", "co-op");
        server.Join(Outbound, "1.1.7", "co-op");

        int PlayersSeenBy(string version)
        {
            var c = server.MasterClient(Outbound, version);
            var list = (Dictionary<string, object>)server.Master.BuildGameListEvent(c.Peer).Parameters[PhotonCodes.Param.GameList];
            return (byte)((Dictionary<object, object>)list["co-op"])[PhotonCodes.RoomProperty.PlayerCount];
        }
        Assert.Equal(2, PlayersSeenBy("1.1.7"));
        Assert.Equal(0, PlayersSeenBy("1.1.6"));
    }

    [Fact]
    public void KeysNeverCollideAndKeepTheirPublicName()
    {
        var keys = new[]
        {
            RoomRegistry.KeyFor(null, null, "Room"),
            RoomRegistry.KeyFor("", "", "Room"),
            RoomRegistry.KeyFor("app", "1.0", "Room"),
            RoomRegistry.KeyFor("app", "1.1", "Room"),
            RoomRegistry.KeyFor(null, null, "[app/1.0] Room"),   // a bare name crafted to look namespaced
            RoomRegistry.KeyFor("a/b", "c", "Room"),
            RoomRegistry.KeyFor("a", "b/c", "Room"),
        };
        Assert.Equal(keys.Length - 1, new HashSet<string>(keys).Count);   // only null and "" are the same
        Assert.Equal("Room", RoomRegistry.PublicName(RoomRegistry.KeyFor("app", "1.0", "Room")));
        Assert.Equal("[app/1.0] Room", RoomRegistry.PublicName(RoomRegistry.KeyFor(null, null, "[app/1.0] Room")));
        Assert.Equal("Room", RoomRegistry.PublicName("Room"));

        var registry = new RoomRegistry();
        var room = registry.GetOrCreate(RoomRegistry.KeyFor("app", "1.3.0-prod", "Q35V9E"));
        Assert.Equal(("app", "1.3.0-prod", "Q35V9E"), (room.AppId, room.AppVersion, room.Name));
    }

    [Fact]
    public void FederatedReportKeepsTheRoomInItsBuild()
    {
        var registry = new RoomRegistry();
        var nodeRoom = new RoomRegistry().GetOrCreate(RoomRegistry.KeyFor(Outbound, "1.1.7", "Q35V9E"));
        nodeRoom.AddActor();

        var report = RoomReport.From("au-sydney", nodeRoom, identity: null);
        Assert.Equal("Q35V9E", report.Room);   // what home is told the room is called
        var wire = JsonSerializer.Serialize(report);
        JsonSerializer.Deserialize<RoomReport>(wire)!.ApplyTo(registry, new NodeDirectory(new HttpClient(), null, null, null, null));

        Assert.NotNull(registry.Find(RoomRegistry.KeyFor(Outbound, "1.1.7", "Q35V9E")));
        Assert.Null(registry.Find("Q35V9E"));
    }

    [Fact]
    public void ConsoleCommandsFindANamespacedRoomByItsPublicName()
    {
        var registry = new RoomRegistry();
        var key = RoomRegistry.KeyFor(WeWereHere, "1.3.0-prod", "Black Ice — Co-op");
        registry.GetOrCreate(key);
        Assert.Equal(key, registry.ResolveName("black ice - co-op"));
    }

    /// <summary>LAN mode skips the Name Server: the client names its build to the Master, whose token then
    /// carries it to the Game server.</summary>
    [Fact]
    public void LanModeCarriesTheBuildFromTheMasterToTheGameServer()
    {
        var registry = new RoomRegistry();
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, registry, allowAnonymousLan: true);
        var game = new GameServerHandler(Secret, registry);
        var m = new Client();
        master.OnOperationRequest(m.Peer, new OperationRequest(PhotonCodes.Op.Authenticate, AuthParams(Outbound, "1.1.7")));
        var token = (string)m.Responses[^1].Parameters[PhotonCodes.Param.Secret];

        var g = new Client();
        game.OnOperationRequest(g.Peer, new OperationRequest(PhotonCodes.Op.Authenticate, new() { { PhotonCodes.Param.Secret, token } }));
        Assert.Equal((Outbound, "1.1.7"), (g.Peer.AppId, g.Peer.AppVersion));
    }

    [Fact]
    public void TokensCarryTheBuildAndOldTokensStillValidate()
    {
        Assert.True(AuthToken.Validate(AuthToken.Mint("openpak:AB", true, Secret, "app|id", "1.3.0-prod"), Secret).TryGet(out var withApp));
        Assert.Equal(new AuthIdentity("openpak:AB", true, "app|id", "1.3.0-prod"), withApp);

        Assert.True(AuthToken.Validate(AuthToken.Mint("openpak:AB", false, Secret), Secret).TryGet(out var withoutApp));
        Assert.Equal(new AuthIdentity("openpak:AB", false), withoutApp);
    }
}
