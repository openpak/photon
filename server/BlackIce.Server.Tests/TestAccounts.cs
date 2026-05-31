using BlackIce.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Tests;

/// <summary>Builds an AccountService backed by an isolated SQLite in-memory DB for handler tests.</summary>
internal static class TestAccounts
{
    public static AccountService Create() => new(NewContext());

    public static RealmService CreateRealms() => new(NewContext());

    private static BlackIceDbContext NewContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();   // kept open for the lifetime of the test process; GC closes it
        var options = new DbContextOptionsBuilder<BlackIceDbContext>().UseSqlite(conn).Options;
        var ctx = new BlackIceDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
