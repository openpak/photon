using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// OpSetProperties (252) handling. Regression cover for the live "kicked back to main menu" bug:
/// the client sets its player properties in-room and, when the server answered -2 "Unknown
/// operation", PUN aborted the room. The server must accept the op (rc=0) and persist the values.
/// </summary>
public class SetPropertiesTests
{
    private static GameServerHandler Handler(out TestDb db, out RoomRegistry registry)
    {
        db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        registry = new RoomRegistry();
        var h = new GameServerHandler("s", registry, allowAnonymousLan: true, realms: new RealmService(db.Context));
        h.EnterRoom(new OperationRequest(227, new() { { 255, "co-op" } }), null);   // room must exist
        return h;
    }

    private static OperationRequest SetProps(Dictionary<object, object> props, int? actorNr) =>
        new(252, actorNr is int nr
            ? new() { { 251, props }, { 254, nr }, { 250, true } }
            : new() { { 251, props }, { 250, true } });

    [Fact]
    public void SetProperties_is_accepted_with_success()
    {
        var h = Handler(out var db, out _);
        using (db)
        {
            var resp = h.SetProperties("co-op", senderActor: 1, SetProps(new() { { "Team", 1 } }, actorNr: 1));
            Assert.Equal(0, resp.ReturnCode);
            Assert.Null(resp.DebugMessage);
        }
    }

    [Fact]
    public void SetProperties_persists_actor_player_properties()
    {
        var h = Handler(out var db, out var registry);
        using (db)
        {
            h.SetProperties("co-op", senderActor: 1, SetProps(new() { { "Team", 2 }, { "PlayerLevel", 7 } }, actorNr: 1));
            var actorProps = registry.Find("co-op")!.ActorProperties(1);
            Assert.Equal(2, actorProps["Team"]);
            Assert.Equal(7, actorProps["PlayerLevel"]);
        }
    }

    [Fact]
    public void SetProperties_without_actor_sets_game_properties()
    {
        var h = Handler(out var db, out var registry);
        using (db)
        {
            h.SetProperties("co-op", senderActor: 1, SetProps(new() { { "Round", 3 } }, actorNr: null));
            Assert.Equal(3, registry.Find("co-op")!.GameProperties["Round"]);
        }
    }

    [Fact]
    public void SetProperties_for_unknown_room_still_acknowledges()
    {
        // An ack (rc=0) keeps the client alive even if we can't resolve the room; failing here is
        // what caused the kick. (roomName null mirrors a peer whose Tag was never set.)
        var h = Handler(out var db, out _);
        using (db)
            Assert.Equal(0, h.SetProperties(null, senderActor: 1, SetProps(new() { { "Team", 1 } }, actorNr: 1)).ReturnCode);
    }
}
