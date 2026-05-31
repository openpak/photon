using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class CommandPermissionTests
{
    private static CommandRegistry Registry(out TestDb db)
    {
        db = new TestDb();
        var accounts = new AccountService(db.Context);
        accounts.ResolveOrCreate("s1", "x");
        return new CommandRegistry().Register(new AccountCommands(accounts));
    }

    [Fact]
    public void A_mod_cannot_run_an_admin_command()
    {
        var reg = Registry(out var db);
        using (db)
        {
            Assert.True(reg.TryExecute("promote s1 2", PlayerLevel.Mod, out var output));
            Assert.Contains("requires Admin", output);
        }
    }

    [Fact]
    public void A_mod_can_run_a_mod_command()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("ban s1", PlayerLevel.Mod, out var output);
            Assert.Contains("banned s1", output);
        }
    }

    [Fact]
    public void Console_can_run_everything()
    {
        var reg = Registry(out var db);
        using (db)
        {
            Assert.True(reg.TryExecute("promote s1 3", PlayerLevel.Console, out var output));
            Assert.Contains("Console", output);
        }
    }

    [Fact]
    public void Help_is_filtered_to_the_callers_tier()
    {
        var reg = Registry(out var db);
        using (db)
        {
            reg.TryExecute("help", PlayerLevel.Mod, out var modHelp);
            Assert.DoesNotContain("promote", modHelp);   // Admin-only
            Assert.Contains("ban", modHelp);             // Mod-allowed

            reg.TryExecute("help", PlayerLevel.Console, out var consoleHelp);
            Assert.Contains("promote", consoleHelp);
        }
    }
}
