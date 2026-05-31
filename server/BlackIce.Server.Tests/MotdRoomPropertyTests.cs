using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class MotdRoomPropertyTests
{
    [Fact]
    public void EnterRoom_includes_resolved_motd_in_game_properties()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var realms = new RealmService(db.Context);
        var motd = new MotdService(db.Context);
        motd.SetGlobal("Welcome, runner.");

        var handler = new GameServerHandler("secret", new RoomRegistry(), allowAnonymousLan: true,
                                            realms: realms, motd: motd);
        var req = new OperationRequest(227, new() { { 255, "co-op" } });
        var (response, _) = handler.EnterRoom(req, null);

        var gameProps = (IDictionary<object, object>)response.Parameters[248];
        Assert.Equal("Welcome, runner.", gameProps["motd"]);
    }

    [Fact]
    public void EnterRoom_omits_motd_when_none_set()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var handler = new GameServerHandler("secret", new RoomRegistry(), allowAnonymousLan: true,
                                            realms: new RealmService(db.Context), motd: new MotdService(db.Context));
        var (response, _) = handler.EnterRoom(new OperationRequest(227, new() { { 255, "co-op" } }), null);
        var gameProps = (IDictionary<object, object>)response.Parameters[248];
        Assert.False(gameProps.ContainsKey("motd"));
    }
}
