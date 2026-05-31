using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class AccountServiceTests
{
    [Fact]
    public void Schema_supports_account_with_profile()
    {
        using var db = new TestDb();
        db.Context.Accounts.Add(new Account { SteamId = "76561198000000001", DisplayName = "Nate" });
        db.Context.SaveChanges();

        using var read = db.NewContext();
        var acct = read.Accounts.Find("76561198000000001");
        Assert.NotNull(acct);
        Assert.Equal(PlayerLevel.Player, acct!.Level);
        Assert.False(acct.IsBanned);
    }

    [Fact]
    public void First_connect_creates_player_account_and_profile()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var acct = svc.ResolveOrCreate("76561198000000002", "Runner");

        Assert.Equal(PlayerLevel.Player, acct.Level);
        Assert.Equal("Runner", acct.DisplayName);
        Assert.NotNull(db.NewContext().Profiles.Find("76561198000000002"));
    }

    [Fact]
    public void Second_connect_updates_lastseen_without_duplicating()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var first = svc.ResolveOrCreate("76561198000000003", "A");
        var firstSeen = first.LastSeenUtc;
        System.Threading.Thread.Sleep(5);
        var again = svc.ResolveOrCreate("76561198000000003", "A-renamed");

        Assert.Equal(1, db.NewContext().Accounts.Count());
        Assert.True(again.LastSeenUtc >= firstSeen);
        Assert.Equal("A-renamed", again.DisplayName);
    }

    [Fact]
    public void SetLevel_changes_permission_tier()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s1", "x");
        Assert.True(svc.SetLevel("s1", PlayerLevel.Admin));
        Assert.Equal(PlayerLevel.Admin, svc.Find("s1")!.Level);
        Assert.False(svc.SetLevel("does-not-exist", PlayerLevel.Mod));
    }

    [Fact]
    public void Ban_and_unban_toggle_flag()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s2", "x");
        Assert.True(svc.SetBanned("s2", true));
        Assert.True(svc.Find("s2")!.IsBanned);
        svc.SetBanned("s2", false);
        Assert.False(svc.Find("s2")!.IsBanned);
    }

    [Fact]
    public void Bootstrap_code_is_generated_once_and_persists()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        var code = svc.EnsureBootstrapCode();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Equal(code, new AccountService(db.NewContext()).EnsureBootstrapCode());
    }

    [Fact]
    public void Claiming_code_promotes_to_console_once()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("owner", "Owner");
        var code = svc.EnsureBootstrapCode();

        Assert.True(svc.ClaimBootstrap("owner", code));
        Assert.Equal(PlayerLevel.Console, svc.Find("owner")!.Level);
        Assert.False(svc.ClaimBootstrap("owner", code));    // one-time
        Assert.False(svc.ClaimBootstrap("owner", "wrong")); // already claimed / bad code
    }
}
