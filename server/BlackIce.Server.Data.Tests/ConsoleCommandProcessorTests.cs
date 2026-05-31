using BlackIce.Server.Data;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class ConsoleCommandProcessorTests
{
    [Fact]
    public void Promote_sets_level()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s1", "x");
        var proc = new ConsoleCommandProcessor(svc);

        var output = proc.Execute("promote s1 2");
        Assert.Contains("Admin", output);
        Assert.Equal(PlayerLevel.Admin, svc.Find("s1")!.Level);
    }

    [Fact]
    public void Ban_marks_account()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s2", "x");
        var proc = new ConsoleCommandProcessor(svc);
        proc.Execute("ban s2");
        Assert.True(svc.Find("s2")!.IsBanned);
    }

    [Fact]
    public void Unknown_command_returns_help_hint()
    {
        using var db = new TestDb();
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context));
        Assert.Contains("help", proc.Execute("frobnicate").ToLowerInvariant());
    }

    [Fact]
    public void Motd_sets_and_reads_global()
    {
        using var db = new TestDb();
        var motd = new MotdService(db.Context);
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context), motd);
        proc.Execute("motd Welcome to the server");
        Assert.Equal("Welcome to the server", motd.GetGlobal());
        Assert.Contains("Welcome to the server", proc.Execute("motd"));
    }

    [Fact]
    public void Realmmotd_sets_named_realm()
    {
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "pvp" });
        db.Context.SaveChanges();
        var motd = new MotdService(db.Context);
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context), motd);
        Assert.Contains("pvp", proc.Execute("realmmotd pvp No mercy"));
        Assert.Equal("No mercy", db.Context.Realms.Find("pvp")!.Motd);
    }

    [Fact]
    public void Realmmotd_handles_realm_name_that_is_substring_of_command()
    {
        // Regression: the old parser sliced text via trimmed.IndexOf(realmName),
        // which matched inside the literal command word "realmmotd" when the
        // realm name ("realm") appeared there first, corrupting the stored text.
        using var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "realm" });
        db.Context.SaveChanges();
        var motd = new MotdService(db.Context);
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context), motd);

        proc.Execute("realmmotd realm hello world");

        Assert.Equal("hello world", db.Context.Realms.Find("realm")!.Motd);
    }

    [Fact]
    public void Demote_alias_maps_to_the_same_handler()
    {
        using var db = new TestDb();
        var svc = new AccountService(db.Context);
        svc.ResolveOrCreate("s3", "x");
        svc.SetLevel("s3", PlayerLevel.Admin);
        var proc = new ConsoleCommandProcessor(svc);

        proc.Execute("demote s3 0");
        Assert.Equal(PlayerLevel.Player, svc.Find("s3")!.Level);
    }

    [Fact]
    public void Help_lists_registered_commands()
    {
        using var db = new TestDb();
        var help = new ConsoleCommandProcessor(new AccountService(db.Context)).Execute("help");
        Assert.Contains("promote", help);
        Assert.Contains("ban", help);
        Assert.Contains("realmmotd", help);
    }

    [Fact]
    public void Too_few_arguments_returns_usage()
    {
        using var db = new TestDb();
        var proc = new ConsoleCommandProcessor(new AccountService(db.Context));
        var output = proc.Execute("promote s1");          // missing the level argument
        Assert.Contains("usage", output);
        Assert.Contains("<0-3>", output);
    }
}
