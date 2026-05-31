using System.Text.Json;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using Microsoft.Extensions.Configuration;

namespace BlackIce.Server.Host;

/// <summary>Server configuration, loaded from blackice.server.json (written with defaults if absent).</summary>
public sealed class ServerConfig
{
    public string AdvertisedHost { get; set; } = "127.0.0.1";
    public bool AllowAnonymousLan { get; set; } = true;

    /// <summary>Token-signing secret, per-role ports, and listener cadence (was hard-coded in Program.cs).</summary>
    public ServerOptions Server { get; set; } = new();

    /// <summary>
    /// Optional global Message of the Day applied on startup. Per-realm overrides live on each
    /// entry in <see cref="Realms"/> (Realm.Motd). A null/empty value here is ignored so it never
    /// wipes a MOTD set live via the `motd` console command. See MotdService.
    /// </summary>
    public string? Motd { get; set; }

    public DatabaseOptions Database { get; set; } = new();

    /// <summary>Realms seeded into the DB on first run (only when the Realms table is empty).</summary>
    public List<Realm> Realms { get; set; } = DefaultRealms();

    public static ServerConfig Load(string path)
    {
        // Resolve a relative path against the EXE directory, not the current working directory:
        // otherwise a config edited next to the binary is silently ignored when the server is
        // launched from elsewhere (e.g. `dotnet run` from the repo root). The log file already
        // anchors to AppContext.BaseDirectory — config + db now match it.
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        // Write a fully-defaulted file on first run so operators have a documented starting point.
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(new ServerConfig(), new JsonSerializerOptions { WriteIndented = true }));

        // Layer the JSON file under BLACKICE_ environment overrides (e.g. BLACKICE_Server__Secret,
        // BLACKICE_Database__Provider) so containers/CI can override any value without editing the
        // file. Bind onto a target whose Realms start empty: ConfigurationBinder appends to existing
        // lists, so the default realms are restored only when neither file nor env supplies any.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: true)
            .AddEnvironmentVariables(prefix: "BLACKICE_")
            .Build();

        var result = new ServerConfig { Realms = new() };
        configuration.Bind(result);
        if (result.Realms.Count == 0) result.Realms = DefaultRealms();
        return result;
    }

    private static List<Realm> DefaultRealms() => new()
    {
        new Realm { Name = "Black Ice — Co-op", DisplayName = "Co-op", Pvp = false, MaxPlayers = 8 },
        new Realm { Name = "Black Ice — PvP", DisplayName = "PvP", Pvp = true, MaxPlayers = 6 },
        new Realm { Name = "Black Ice — Hardcore", DisplayName = "Hardcore", HackDifficultyIncrease = 5, MaxPlayers = 4 },
    };
}
