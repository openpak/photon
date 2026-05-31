using BlackIce.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Tests;

/// <summary>Creates an isolated SQLite in-memory BlackIceDbContext (connection kept open per instance).</summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public BlackIceDbContext Context { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<BlackIceDbContext>().UseSqlite(_conn).Options;
        Context = new BlackIceDbContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
