using OpenPak.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace OpenPak.Server.Data.Tests;

/// <summary>Creates an isolated SQLite in-memory OpenPakDbContext (connection kept open per instance).</summary>
public sealed class TestDb : IDisposable
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

    /// <summary>A fresh context over the same in-memory DB (simulates a new request/connection).</summary>
    public OpenPakDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<OpenPakDbContext>().UseSqlite(_conn).Options;
        return new OpenPakDbContext(options);
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
