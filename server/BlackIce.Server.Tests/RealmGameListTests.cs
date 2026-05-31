using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RealmGameListTests
{
    [Fact]
    public void GameList_lists_each_visible_realm_with_props()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "PvP Arena", Pvp = true, HackDifficultyIncrease = 3, MaxPlayers = 6 });
        realms.Upsert(new Realm { Name = "Hidden", IsVisible = false });

        var master = new MasterServerHandler("127.0.0.1:5056", "secret", new RoomRegistry(), realms: realms);
        var ev = master.BuildGameListEvent();
        var rooms = (Dictionary<string, object>)ev.Parameters[222];

        Assert.True(rooms.ContainsKey("PvP Arena"));
        Assert.False(rooms.ContainsKey("Hidden"));
        var props = (Dictionary<object, object>)rooms["PvP Arena"];
        Assert.Equal(true, props["PVP"]);
        Assert.Equal(3, props["HackDifficultyIncrease"]);
        Assert.Equal((byte)6, props[(byte)255]);     // MaxPlayers
    }
}
