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

    [Fact]
    public void Lobby_player_count_excludes_bots_by_default_and_includes_them_when_opted_in()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Arena", MaxPlayers = 8 });

        // Default: no bot-count source -> only real players (0 here) are advertised.
        var off = new MasterServerHandler("127.0.0.1:5056", "secret", new RoomRegistry(), realms: realms);
        var offProps = (Dictionary<object, object>)((Dictionary<string, object>)off.BuildGameListEvent().Parameters[222])["Arena"];
        Assert.Equal((byte)0, offProps[(byte)252]);   // PlayerCount

        // Opted in: the wired bot-count func adds the realm's bots to the advertised count.
        var on = new MasterServerHandler("127.0.0.1:5056", "secret", new RoomRegistry(), realms: realms,
                                         lobbyBotCount: room => room == "Arena" ? 4 : 0);
        var onProps = (Dictionary<object, object>)((Dictionary<string, object>)on.BuildGameListEvent().Parameters[222])["Arena"];
        Assert.Equal((byte)4, onProps[(byte)252]);
    }
}
