using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RealmJoinTests
{
    private static GameServerHandler Make(RealmService realms) =>
        new("secret", new RoomRegistry(), accounts: null, realms: realms);

    [Fact]
    public void EnterRoom_applies_realm_ruleset_to_game_properties()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Hard", Pvp = true, HackDifficultyIncrease = 5, MaxPlayers = 4 });
        var (resp, join) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Hard" } }), null);

        Assert.Equal(0, resp.ReturnCode);
        var props = (Dictionary<object, object>)resp.Parameters[248];   // GameProperties
        Assert.Equal(true, props["PVP"]);
        Assert.Equal(5, props["HackDifficultyIncrease"]);
        Assert.Equal(255, join.Code);
    }

    [Fact]
    public void EnterRoom_rejects_unknown_realm()
    {
        var realms = TestAccounts.CreateRealms();
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Nope" } }), null);
        Assert.NotEqual(0, resp.ReturnCode);
    }

    [Fact]
    public void EnterRoom_rejects_when_realm_is_full()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Duo", MaxPlayers = 2 });
        var handler = Make(realms);   // one handler -> one registry -> shared room
        var req = new OperationRequest(227, new() { { 255, "Duo" } });

        Assert.Equal(0, handler.EnterRoom(req, null).Response.ReturnCode);    // 1/2
        Assert.Equal(0, handler.EnterRoom(req, null).Response.ReturnCode);    // 2/2
        Assert.Equal(-6, handler.EnterRoom(req, null).Response.ReturnCode);   // full
    }

    [Fact]
    public void EnterRoom_rejects_wrong_password()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Locked", Password = "secret123" });
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Locked" } }), "wrong");
        Assert.NotEqual(0, resp.ReturnCode);

        var (ok, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Locked" } }), "secret123");
        Assert.Equal(0, ok.ReturnCode);
    }
}
