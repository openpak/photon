using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class MotdServiceTests
{
    [Fact]
    public void Realm_override_wins_over_global()
    {
        using var db = new TestDb();
        var svc = new MotdService(db.Context);
        svc.SetGlobal("global");
        var realm = new Realm { Name = "pvp", Motd = "realm-specific" };
        Assert.Equal("realm-specific", svc.Resolve(realm));
    }

    [Fact]
    public void Falls_back_to_global_when_realm_override_blank()
    {
        using var db = new TestDb();
        var svc = new MotdService(db.Context);
        svc.SetGlobal("global");
        Assert.Equal("global", svc.Resolve(new Realm { Name = "co-op", Motd = "  " }));
        Assert.Equal("global", svc.Resolve(null));
    }

    [Fact]
    public void Returns_null_when_nothing_set()
    {
        using var db = new TestDb();
        var svc = new MotdService(db.Context);
        Assert.Null(svc.Resolve(null));
    }

    [Fact]
    public void SetRealm_persists_and_reports_missing()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op" });
        db.Context.SaveChanges();
        var svc = new MotdService(db.Context);
        Assert.True(svc.SetRealm("co-op", "welcome").IsOk);
        Assert.Equal("welcome", db.Context.Realms.Find("co-op")!.Motd);
        Assert.True(svc.SetRealm("nope", "x").IsFail);
    }
}
