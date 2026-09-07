using OpenPak.Photon;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

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
    public void EnterRoom_allows_ad_hoc_room_not_matching_any_realm()
    {
        // A room name that isn't a configured realm is a dynamic/ad-hoc room (e.g. a Fusion client's
        // own session code) — let through open, same as when no realms are configured at all.
        var realms = TestAccounts.CreateRealms();
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Nope" } }), null);
        Assert.Equal(0, resp.ReturnCode);
    }

    [Fact]
    public void EnterRoom_stamps_master_client_id_as_lowest_actor()
    {
        // Its absence reads to a Fusion client as "no valid master for this room" and it immediately
        // aborts the join (OpLeave) right after entering — see GameServerHandler.EnterRoom.
        var handler = Make(TestAccounts.CreateRealms());
        var req = new OperationRequest(227, new() { { 255, "Adhoc" } });

        var first = handler.EnterRoom(req, null);
        Assert.Equal(1, ((Dictionary<object, object>)first.Response.Parameters[248])[PhotonCodes.RoomProperty.MasterClientId]);

        var second = handler.EnterRoom(req, null);   // second joiner: master stays the first (lowest) actor
        Assert.Equal(1, ((Dictionary<object, object>)second.Response.Parameters[248])[PhotonCodes.RoomProperty.MasterClientId]);
    }

    [Fact]
    public void EnterRoom_rejects_disabled_realm()
    {
        var realms = TestAccounts.CreateRealms();
        realms.Upsert(new Realm { Name = "Retired", IsEnabled = false });
        var (resp, _) = Make(realms).EnterRoom(new OperationRequest(227, new() { { 255, "Retired" } }), null);
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
