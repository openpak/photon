using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "Sqlite";              // "Sqlite" | "MySql"
    public string ConnectionString { get; set; } = "Data Source=blackice.db";

    /// <summary>Builds a context configured for the selected provider and ensures the schema exists.</summary>
    public BlackIceDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<BlackIceDbContext>();
        switch (Provider.ToLowerInvariant())
        {
            case "mysql":
                builder.UseMySql(ConnectionString, ServerVersion.AutoDetect(ConnectionString));
                break;
            case "sqlite":
            default:
                builder.UseSqlite(AnchorSqliteFile(ConnectionString));
                break;
        }
        var ctx = new BlackIceDbContext(builder.Options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    /// <summary>
    /// Resolves a relative SQLite Data Source file against the EXE directory so the database lives
    /// next to the binary regardless of the launch working directory (matching config + log files).
    /// Leaves <c>:memory:</c> and already-absolute paths untouched — so in-memory test contexts and
    /// explicit paths are unaffected.
    /// </summary>
    public static string AnchorSqliteFile(string connectionString)
    {
        var b = new SqliteConnectionStringBuilder(connectionString);
        var src = b.DataSource;
        if (string.IsNullOrEmpty(src) || src == ":memory:" || src.StartsWith("file::memory:") || Path.IsPathRooted(src))
            return connectionString;
        b.DataSource = Path.Combine(AppContext.BaseDirectory, src);
        return b.ToString();
    }
}
