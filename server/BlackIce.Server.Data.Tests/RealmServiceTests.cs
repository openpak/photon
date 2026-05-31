using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class RealmServiceTests
{
    [Fact]
    public void SeedDefaults_inserts_only_when_table_empty()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.SeedDefaults(new[] { new Realm { Name = "PvE" }, new Realm { Name = "PvP", Pvp = true } });
        svc.SeedDefaults(new[] { new Realm { Name = "Extra" } });   // table not empty -> no-op
        Assert.Equal(2, svc.ListEnabled().Count);
        Assert.Null(svc.Get("Extra"));
    }

    [Fact]
    public void ListVisible_excludes_hidden_and_disabled()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.Upsert(new Realm { Name = "Shown", IsVisible = true, IsEnabled = true });
        svc.Upsert(new Realm { Name = "Hidden", IsVisible = false, IsEnabled = true });
        svc.Upsert(new Realm { Name = "Off", IsVisible = true, IsEnabled = false });
        var visible = svc.ListVisible();
        Assert.Single(visible);
        Assert.Equal("Shown", visible[0].Name);
    }

    [Fact]
    public void Upsert_updates_existing_and_delete_removes()
    {
        using var db = new TestDb();
        var svc = new RealmService(db.Context);
        svc.Upsert(new Realm { Name = "R", Pvp = false });
        svc.Upsert(new Realm { Name = "R", Pvp = true });
        Assert.True(svc.Get("R")!.Pvp);
        Assert.True(svc.Delete("R"));
        Assert.Null(svc.Get("R"));
    }
}
