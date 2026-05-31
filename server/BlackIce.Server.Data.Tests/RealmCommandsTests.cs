using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class RealmCommandsTests
{
    private static CommandRegistry Registry(out TestDb db)
    {
        db = new TestDb();
        var realms = new RealmService(db.Context);
        realms.Upsert(new Realm { Name = "co-op", DisplayName = "Co-op", MaxPlayers = 8 });
        realms.Upsert(new Realm { Name = "off", DisplayName = "Off", IsEnabled = false });
        return new CommandRegistry().Register(new RealmCommands(realms));
    }

    [Fact]
    public void Realms_lists_all_realms_including_disabled()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("realms", PlayerLevel.Console, out var o);
            Assert.Contains("co-op", o);
            Assert.Contains("DISABLED", o);   // the disabled realm is shown
        }
    }

    [Fact]
    public void Realm_shows_detail_or_reports_missing()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("realm co-op", PlayerLevel.Console, out var ok);
            Assert.Contains("maxPlayers=8", ok);
            reg.TryExecute("realm ghost", PlayerLevel.Console, out var missing);
            Assert.Contains("no such realm", missing);
        }
    }

    [Fact]
    public void Setmode_changes_the_mode_and_rejects_unknown()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("setmode co-op TeamVsTeam", PlayerLevel.Console, out var ok);
            Assert.Contains("TeamVsTeam", ok);
            reg.TryExecute("realm co-op", PlayerLevel.Console, out var detail);
            Assert.Contains("mode=TeamVsTeam", detail);

            reg.TryExecute("setmode co-op Nonsense", PlayerLevel.Console, out var bad);
            Assert.Contains("unknown mode", bad);
        }
    }

    [Fact]
    public void Create_and_delete_a_realm()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("realmcreate Arena", PlayerLevel.Console, out var created);
            Assert.Contains("created realm", created);
            reg.TryExecute("realm Arena", PlayerLevel.Console, out var detail);
            Assert.Contains("Arena", detail);
            reg.TryExecute("realmdelete Arena", PlayerLevel.Console, out var deleted);
            Assert.Contains("deleted realm", deleted);
            reg.TryExecute("realm Arena", PlayerLevel.Console, out var gone);
            Assert.Contains("no such realm", gone);
        }
    }

    [Fact]
    public void Permission_gate_on_realm_admin_commands()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("setmode co-op Coop", PlayerLevel.Mod, out var denied);
            Assert.Contains("requires Admin", denied);
        }
    }
}
