using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenPak.Server.Data;

/// <summary>
/// The server's one store: Postgres. There is no file or in-memory fallback; an unset connection string
/// is a startup error (see <see cref="Validate"/>).
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Environment variable that carries the Postgres DSN (URL or Npgsql key=value form).</summary>
    public const string EnvVar = "PHOTON_DATABASE_URL";

    public string ConnectionString { get; set; } = "";

    /// <summary>When true (the default), missing tables are created on startup. Set false to manage the schema out of band.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Fills an empty connection string from <see cref="EnvVar"/>.</summary>
    public void ApplyEnvironment(Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) && env(EnvVar) is { Length: > 0 } v) ConnectionString = v;
    }

    /// <summary>Returns human-readable validation errors; empty means usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString))
            errors.Add($"no Postgres connection string: set {EnvVar} (or Database.ConnectionString). The server has no file or memory fallback.");
        return errors;
    }

    /// <summary>Builds a context and initializes its schema per <see cref="AutoMigrate"/>.</summary>
    public OpenPakDbContext CreateContext()
    {
        var ctx = new OpenPakDbContext(BuildOptions());
        InitializeSchema(ctx);
        return ctx;
    }

    public DbContextOptions<OpenPakDbContext> BuildOptions()
    {
        var builder = new DbContextOptionsBuilder<OpenPakDbContext>();
        Configure(builder);
        return builder.Options;
    }

    /// <summary>Applies Npgsql to a context options builder (shared with the host's <c>AddDbContextFactory</c>).</summary>
    public void Configure(DbContextOptionsBuilder builder) => builder.UseNpgsql(Normalize(ConnectionString));

    /// <summary>Creates the tables on a fresh database, then the raw-SQL rewards table. Idempotent.</summary>
    public void InitializeSchema(OpenPakDbContext ctx)
    {
        if (!AutoMigrate) return;
        ctx.Database.EnsureCreated();
        // 2026-11-05 (prds#131): the name column held the Steam id again; names live in the core account.
        ctx.Database.ExecuteSqlRaw("ALTER TABLE IF EXISTS \"Accounts\" DROP COLUMN IF EXISTS \"DisplayName\"");
        ctx.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS rewards_state (name VARCHAR(64) NOT NULL PRIMARY KEY, body TEXT NOT NULL)");
    }

    /// <summary>Accepts <c>postgres://user:pw@host:port/db?sslmode=…</c> or an Npgsql string; caps the pool at 3.</summary>
    public static string Normalize(string dsn)
    {
        NpgsqlConnectionStringBuilder b;
        if (dsn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || dsn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(dsn);
            b = new NpgsqlConnectionStringBuilder
            {
                Host = u.Host,
                Port = u.Port > 0 ? u.Port : 5432,
                Database = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')),
            };
            if (!string.IsNullOrEmpty(u.UserInfo))
            {
                var parts = u.UserInfo.Split(':', 2);
                b.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1) b.Password = Uri.UnescapeDataString(parts[1]);
            }
            foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                if (p[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && p.Length == 2 && Enum.TryParse<SslMode>(p[1], true, out var m)) b.SslMode = m;
            }
        }
        else b = new NpgsqlConnectionStringBuilder(dsn);
        b.MaxPoolSize = 3;
        b.MinPoolSize = 0;
        return b.ToString();
    }
}
