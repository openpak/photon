using BlackIce.Server.Core;
using BlackIce.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlackIce.Server.Host;

/// <summary>
/// One-time startup work that must complete before any listener accepts traffic: bring the schema up
/// to date, log the bootstrap code, seed the configured realms, and apply MOTDs from config. Runs
/// single-threaded against contexts from the shared factory.
/// </summary>
public static class StartupInitializer
{
    public static void Run(IServiceProvider services, ServerConfig config, string logPath)
    {
        var dbf = services.GetRequiredService<IDbContextFactory<BlackIceDbContext>>();

        // Schema first (migrate on SQLite / ensure on others), then everything else can query safely.
        using (var schemaCtx = dbf.CreateDbContext())
            config.Database.InitializeSchema(schemaCtx);

        Log.Info("HOST", $"BlackIce.Server starting — DB {config.Database.Provider}, advertising {config.AdvertisedHost}, " +
                         $"anonLan={config.AllowAnonymousLan}, logLevel={Log.Level}");
        Log.Info("HOST", $"log file: {logPath}");

        using var ctx = dbf.CreateDbContext();
        var accounts = new AccountService(ctx);
        Log.Info("HOST", $"One-time bootstrap code: {accounts.EnsureBootstrapCode()}");

        var realms = new RealmService(ctx);
        var motd = new MotdService(ctx);
        realms.SeedDefaults(config.Realms);

        // Apply MOTDs from config, each guarded by a non-empty check so an absent value never wipes a
        // MOTD set live via the console (config is authoritative only when it specifies a value).
        if (!string.IsNullOrWhiteSpace(config.Motd))
        {
            motd.SetGlobal(config.Motd);
            Log.Info("HOST", $"global MOTD from config: \"{config.Motd}\"");
        }
        foreach (var r in config.Realms.Where(r => !string.IsNullOrWhiteSpace(r.Motd)))
            if (motd.SetRealm(r.Name, r.Motd).IsOk) Log.Info("HOST", $"realm MOTD from config: {r.Name} -> \"{r.Motd}\"");

        Log.Info("HOST", $"Realms: {string.Join(", ", realms.ListVisible().Select(r => r.Name))}");
    }
}
