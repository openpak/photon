using System.Collections;
using System.Collections.Generic;
using System.Net;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// Quick match through OpJoinRandomGame. Unrailed! (Photon C++ SDK) asks for a random room in a SQL
/// lobby with filters on room properties C0..C3 and creates a public room when told none matches.
/// </summary>
public class JoinRandomGameTests
{
    private const string Secret = "s";
    private const string Unrailed = "9d45adf2-e83f-4281-b9f5-02728f813f80";

    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private readonly RoomRegistry _registry = new();
    private readonly MasterServerHandler _master;

    public JoinRandomGameTests() =>
        _master = new MasterServerHandler("127.0.0.1:5056", Secret, _registry, allowAnonymousLan: true);

    private Room Room(string name, int players, string version = "1.0.12", Hashtable? props = null)
    {
        var room = _registry.GetOrCreate(RoomRegistry.KeyFor(Unrailed, version, name));
        for (int i = 0; i < players; i++) room.AddActor();
        if (props is not null) room.SetProperties(null, props);
        return room;
    }

    private OperationResponse JoinRandom(string? sql = null, Hashtable? expected = null, string version = "1.0.12")
    {
        var responses = new List<OperationResponse>();
        var peer = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { })
        {
            OnResponse = responses.Add,
        };
        _master.OnOperationRequest(peer, new OperationRequest(PhotonCodes.Op.Authenticate, new()
        {
            { PhotonCodes.Param.ApplicationId, Unrailed }, { PhotonCodes.Param.AppVersion, version },
        }));
        Assert.Equal(0, responses[^1].ReturnCode);

        var p = new Dictionary<byte, object> { { PhotonCodes.Param.LobbyType, (byte)2 } };
        if (sql is not null) p[PhotonCodes.Param.Data] = sql;
        if (expected is not null) p[PhotonCodes.Param.GameProperties] = expected;
        _master.OnOperationRequest(peer, new OperationRequest(PhotonCodes.Op.JoinRandomGame, p));
        return responses[^1];
    }

    private static Hashtable Props(params (object Key, object Value)[] kv)
    {
        var h = new Hashtable();
        foreach (var (k, v) in kv) h[k] = v;
        return h;
    }

    [Fact]
    public void NoRoomMeansNoMatchSoTheClientCreatesOne()
    {
        var r = JoinRandom("C0 < 4 AND C1 = 2");
        Assert.Equal(MasterServerHandler.NoRandomMatchFound, r.ReturnCode);
    }

    [Fact]
    public void FilterPicksTheMatchingRoom()
    {
        Room("versus", 1, props: Props(("C0", 1), ("C1", 3), ("C3", 0), ((byte)255, (byte)4)));
        Room("coop", 2, props: Props(("C0", 2), ("C1", 2), ("C3", 0), ((byte)255, (byte)4)));

        var r = JoinRandom("C0 < 4 AND C1 = 2 AND (C3 = 0 OR C3 = 4 )");
        Assert.Equal(0, r.ReturnCode);
        Assert.Equal("coop", r.Parameters[PhotonCodes.Param.RoomName]);
        Assert.Equal("127.0.0.1:5056", r.Parameters[PhotonCodes.Param.Address]);
    }

    [Fact]
    public void ClosedFullEmptyAndOtherBuildRoomsAreSkipped()
    {
        Room("closed", 1, props: Props(("C1", 2), ((byte)253, false)));
        Room("full", 4, props: Props(("C1", 2), ((byte)255, (byte)4)));
        Room("empty", 0, props: Props(("C1", 2)));
        Room("old", 1, version: "1.0.11", props: Props(("C1", 2)));
        Assert.Equal(MasterServerHandler.NoRandomMatchFound, JoinRandom("C1 = 2").ReturnCode);

        Room("open", 3, props: Props(("C1", 2), ((byte)255, (byte)4)));
        Assert.Equal("open", JoinRandom("C1 = 2").Parameters[PhotonCodes.Param.RoomName]);
    }

    [Fact]
    public void FullestMatchingRoomWinsAndExpectedPropertiesCount()
    {
        Room("one", 1, props: Props(("C1", 2), ("mode", "a")));
        Room("three", 3, props: Props(("C1", 2), ("mode", "b")));
        Assert.Equal("three", JoinRandom().Parameters[PhotonCodes.Param.RoomName]);
        Assert.Equal("one", JoinRandom(expected: Props(("mode", "a"))).Parameters[PhotonCodes.Param.RoomName]);
    }

    [Theory]
    [InlineData("C0 < 3 AND C1 = 2", true)]
    [InlineData("C0 < 2", false)]
    [InlineData("C0 >= 2 AND C0 <= 2 AND C0 <> 1", true)]
    [InlineData("C1 = 2 AND NOT (C2 = \"me\")", false)]
    [InlineData("C1 = 2 AND NOT (C2 = 'you')", true)]
    [InlineData("(C3 = 0 OR C3 = 4 )", true)]
    [InlineData("C9 = 1", false)]          // a missing column is NULL: never equal
    [InlineData("NOT (C9 = 1)", false)]    // ... and never unequal either
    [InlineData("C0 < ", false)]           // unparsable matches nothing
    [InlineData("c1 = 2 and c0 = 2", false)] // column names are case-sensitive property keys
    public void SqlFilter(string filter, bool matches)
    {
        var props = new Dictionary<object, object> { { "C0", 2 }, { "C1", (byte)2 }, { "C2", "me" }, { "C3", 0L } };
        Assert.Equal(matches, SqlLobbyFilter.Matches(filter, props));
    }
}
