using System.Text.Json;
using BlackIce.Server.Data;

namespace BlackIce.Server.Host;

/// <summary>Server configuration, loaded from blackice.server.json (written with defaults if absent).</summary>
public sealed class ServerConfig
{
    public string AdvertisedHost { get; set; } = "127.0.0.1";
    public bool AllowAnonymousLan { get; set; } = true;

    /// <summary>
    /// Optional global Message of the Day applied on startup. Per-realm overrides live on each
    /// entry in <see cref="Realms"/> (Realm.Motd). A null/empty value here is ignored so it never
    /// wipes a MOTD set live via the `motd` console command. See MotdService.
    /// </summary>
    public string? Motd { get; set; }

    public DatabaseOptions Database { get; set; } = new();

    /// <summary>Realms seeded into the DB on first run (only when the Realms table is empty).</summary>
    public List<Realm> Realms { get; set; } = new()
    {
        new Realm { Name = "Black Ice — Co-op", DisplayName = "Co-op", Pvp = false, MaxPlayers = 8 },
        new Realm { Name = "Black Ice — PvP", DisplayName = "PvP", Pvp = true, MaxPlayers = 6 },
        new Realm { Name = "Black Ice — Hardcore", DisplayName = "Hardcore", HackDifficultyIncrease = 5, MaxPlayers = 4 },
    };

    public static ServerConfig Load(string path)
    {
        // Resolve a relative path against the EXE directory, not the current working directory:
        // otherwise a config edited next to the binary is silently ignored when the server is
        // launched from elsewhere (e.g. `dotnet run` from the repo root). The log file already
        // anchors to AppContext.BaseDirectory — config + db now match it.
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(path))
        {
            var def = new ServerConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
            return def;
        }
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }
}
