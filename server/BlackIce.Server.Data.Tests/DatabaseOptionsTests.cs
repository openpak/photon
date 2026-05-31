using System;
using System.IO;
using System.Linq;
using BlackIce.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlackIce.Server.Data.Tests;

/// <summary>
/// SQLite Data Source anchoring: a relative db file must resolve next to the EXE (AppContext.
/// BaseDirectory), not the launch working directory — otherwise the server reads/writes a
/// different blackice.db depending on where it was started from.
/// </summary>
public class DatabaseOptionsTests
{
    [Fact]
    public void Relative_data_source_is_anchored_to_base_directory()
    {
        var anchored = DatabaseOptions.AnchorSqliteFile("Data Source=blackice.db");
        var src = new SqliteConnectionStringBuilder(anchored).DataSource;
        Assert.True(Path.IsPathRooted(src));
        Assert.StartsWith(AppContext.BaseDirectory, src);
        Assert.EndsWith("blackice.db", src);
    }

    [Fact]
    public void In_memory_data_source_is_untouched()
    {
        const string cs = "Data Source=:memory:";
        Assert.Equal(":memory:", new SqliteConnectionStringBuilder(DatabaseOptions.AnchorSqliteFile(cs)).DataSource);
    }

    [Fact]
    public void Absolute_data_source_is_untouched()
    {
        var abs = Path.Combine(Path.GetTempPath(), "explicit.db");
        var src = new SqliteConnectionStringBuilder(DatabaseOptions.AnchorSqliteFile($"Data Source={abs}")).DataSource;
        Assert.Equal(abs, src);
    }

    [Fact]
    public void CreateContext_applies_migrations_and_yields_a_usable_schema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"blackice-mig-{Guid.NewGuid():N}.db");
        var opts = new DatabaseOptions { Provider = "Sqlite", ConnectionString = $"Data Source={dbPath}" };
        try
        {
            using (var ctx = opts.CreateContext())
            {
                // The committed migration was applied (recorded in __EFMigrationsHistory) ...
                Assert.Contains(ctx.Database.GetAppliedMigrations(), m => m.EndsWith("InitialCreate"));
                // ... and the resulting schema round-trips data.
                ctx.Realms.Add(new Realm { Name = "r", DisplayName = "R" });
                ctx.SaveChanges();
            }
            using var reopened = opts.CreateContext();   // re-applying migrations on an up-to-date DB is a no-op
            Assert.Equal(1, reopened.Realms.Count());
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-shm", dbPath + "-wal" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
