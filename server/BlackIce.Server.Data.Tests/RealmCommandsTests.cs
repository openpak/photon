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
}
