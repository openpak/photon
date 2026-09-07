using OpenPak.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace OpenPak.Server.Tests;

/// <summary>Creates an isolated SQLite in-memory OpenPakDbContext (connection kept open per instance).</summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public OpenPakDbContext Context { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<OpenPakDbContext>().UseSqlite(_conn).Options;
        Context = new OpenPakDbContext(options);
        Context.Database.EnsureCreated();
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
