using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

public sealed class DatabaseOptions
{
    public string Provider { get; set; } = "Sqlite";              // "Sqlite" | "MySql"
    public string ConnectionString { get; set; } = "Data Source=blackice.db";

    /// <summary>
    /// When true (the default), the schema is brought up to date on startup: SQLite applies the
    /// committed EF Core migrations (<see cref="RelationalDatabaseFacadeExtensions.Migrate"/>); other
    /// providers fall back to <c>EnsureCreated</c>. Set false to manage the schema out of band — e.g.
    /// running <c>dotnet ef database update</c> as a deliberate deploy step.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Builds a context for the selected provider and initializes its schema per <see cref="AutoMigrate"/>.</summary>
    public BlackIceDbContext CreateContext()
    {
        var ctx = new BlackIceDbContext(BuildOptions());
        InitializeSchema(ctx);
        return ctx;
    }

    /// <summary>
    /// Builds the provider-configured options without touching the database. Exposed so the host can
    /// register a pooled <c>IDbContextFactory&lt;BlackIceDbContext&gt;</c> over the same configuration.
    /// </summary>
    public DbContextOptions<BlackIceDbContext> BuildOptions()
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
        return builder.Options;
    }

    /// <summary>
    /// Brings a fresh or existing database up to the current schema. SQLite (the default) ships
    /// committed migrations and applies any pending ones in order, recording them in
    /// <c>__EFMigrationsHistory</c> — so schema changes are versioned and replayable. The MySQL
    /// provider has no migrations of its own yet and uses <c>EnsureCreated</c> until provider-specific
    /// migrations are generated; both keep a new database self-initializing on first run.
    /// </summary>
    public void InitializeSchema(BlackIceDbContext ctx)
    {
        if (!AutoMigrate) return;
        if (string.Equals(Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            ctx.Database.Migrate();
        else
            ctx.Database.EnsureCreated();
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
