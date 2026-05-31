using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RoleHandlerTests
{
    const string Secret = "test-secret";

    [Fact]
    public void NameServer_authenticate_returns_master_address_token_userid()
    {
        var ns = new NameServerHandler("127.0.0.1:5055", Secret, TestAccounts.Create());
        var resp = ns.Authenticate(new OperationRequest(230, new() { { 220, "v1" }, { 210, "us/*" } }));

        Assert.Equal(0, resp.ReturnCode);
        Assert.Equal("127.0.0.1:5055", resp.Parameters[230]);
        Assert.True(resp.Parameters.ContainsKey(221));
        Assert.True(resp.Parameters.ContainsKey(225));
    }

    [Fact]
    public void Token_minted_by_nameserver_validates_on_master()
    {
        var ns = new NameServerHandler("127.0.0.1:5055", Secret, TestAccounts.Create());
        var token = (string)ns.Authenticate(new OperationRequest(230, new())).Parameters[221];

        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry());
        var resp = master.Authenticate(new OperationRequest(230, new() { { 221, token } }));
        Assert.Equal(0, resp.ReturnCode);
    }

    [Fact]
    public void Master_rejects_forged_token()
    {
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry());
        var resp = master.Authenticate(new OperationRequest(230, new() { { 221, "user.forged" } }));
        Assert.Equal(-1, resp.ReturnCode);
    }

    [Fact]
    public void Master_rejects_tokenless_auth_by_default()
    {
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry());
        // No token, anonymous not allowed -> rejected (secure default).
        Assert.Equal(-1, master.Authenticate(new OperationRequest(230, new()), allowAnonymous: false).ReturnCode);
    }

    [Fact]
    public void Master_allows_tokenless_auth_only_when_explicitly_enabled()
    {
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry());
        var resp = master.Authenticate(new OperationRequest(230, new()), allowAnonymous: true);
        Assert.Equal(0, resp.ReturnCode);
        Assert.True(resp.Parameters.ContainsKey(221)); // minted token handed back for the Game hop
    }

    [Fact]
    public void Game_rejects_tokenless_auth_by_default()
    {
        var game = new GameServerHandler(Secret, new RoomRegistry());
        Assert.Equal(-1, game.Authenticate(new OperationRequest(230, new()), allowAnonymous: false).ReturnCode);
    }

    [Fact]
    public void Master_creategame_returns_game_server_address()
    {
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry());
        var resp = master.CreateGame(new OperationRequest(227, new() { { 255, "Black Ice Public Game #1" } }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.Equal("127.0.0.1:5056", resp.Parameters[230]);
    }

    [Fact]
    public void Game_enterroom_assigns_actor_and_raises_join()
    {
        var registry = new RoomRegistry();
        var game = new GameServerHandler(Secret, registry);
        var (resp, join) = game.EnterRoom(new OperationRequest(227, new() { { 255, "Room #1" } }), null);

        Assert.Equal(0, resp.ReturnCode);
        Assert.Equal(1, resp.Parameters[254]);     // first actor number
        Assert.Equal(255, join.Code);              // Join event
        Assert.Equal(new[] { 1 }, (int[])join.Parameters[252]);
    }

    [Fact]
    public void Game_second_player_gets_actor_two_and_full_actor_list()
    {
        var registry = new RoomRegistry();
        var game = new GameServerHandler(Secret, registry);
        game.EnterRoom(new OperationRequest(227, new() { { 255, "Room #1" } }), null);
        var (_, join2) = game.EnterRoom(new OperationRequest(226, new() { { 255, "Room #1" } }), null);

        Assert.Equal(2, join2.Parameters[254]);
        Assert.Equal(new[] { 1, 2 }, (int[])join2.Parameters[252]);
    }
}
