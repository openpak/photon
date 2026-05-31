# Configuration

The server reads **`blackice.server.json`** from the executable directory. If the file is missing it
is written with defaults on first run, so the simplest setup is: start the server once, then edit the
generated file. Every value can also be overridden by an environment variable (see
[Environment overrides](#environment-overrides)) — handy for containers and CI where you don't want to
ship a file.

Configuration is validated at startup. A hard error (empty secret, out-of-range or duplicate ports,
nonsensical listener timings) is logged and the server **refuses to start** rather than failing
obscurely once a client connects. Running on the shipped placeholder secret logs a warning.

## Reference

```jsonc
{
  // IP/host advertised to clients in the Name->Master->Game handoff. Set this to the address
  // other machines reach this server on (LAN IP, or public IP). Also overridable as the first
  // command-line argument: `BlackIce.Server.Host 192.168.1.10`.
  "AdvertisedHost": "127.0.0.1",

  // When true, tokenless LAN-mode auth is accepted from loopback/private-range peers only.
  // Set false (or pass --require-token) to require the full Name Server token flow.
  "AllowAnonymousLan": true,

  // Optional global Message of the Day applied on startup. Empty/absent leaves any MOTD set live
  // via the `motd` console command intact. Per-realm overrides live on each realm's "Motd".
  "Motd": null,

  "Server": {
    // HMAC key the Name/Master/Game roles sign and validate auth tokens with. CHANGE THIS before
    // exposing the server publicly — the default is flagged with a startup warning.
    "Secret": "change-me-platform-secret",

    // UDP ports for the three Photon roles. Must be distinct and in 1..65535.
    "Ports": { "NameServer": 5058, "MasterServer": 5055, "GameServer": 5056 },

    // Keepalive / dead-peer cleanup cadence, in seconds. Defaults match Photon's behavior:
    // run maintenance every 1s, actively ping a peer silent for 3s, evict one silent for 10s.
    // DeadTimeoutSeconds must be greater than PingQuietSeconds.
    "Listener": { "MaintenanceSeconds": 1, "PingQuietSeconds": 3, "DeadTimeoutSeconds": 10 },

    // Server-authority / anti-cheat validators on the relay. Detection-only by default: a violation
    // is logged and the event is still forwarded. Set Enforce:true to also DROP the offending event
    // once thresholds are tuned against live play. Thresholds are generous to avoid false positives.
    "Anticheat": {
      "Enforce": false,
      "MaxDamagePerHit": 100000.0,        // single-hit damage ceiling
      "MaxSpeedUnitsPerSecond": 200.0,    // movement speedhack ceiling
      "MaxTeleportDistance": 500.0,       // single-step position jump ceiling
      "RateWindowSeconds": 1.0,           // sliding window for the per-actor rate checks below
      "MaxEventsPerWindow": 200,          // per-actor event flood ceiling
      "MaxHitsPerWindow": 30,             // per-actor damage-RPC rate (rapid-fire / aimbot)
      "MaxDamagePerWindow": 5000.0,       // per-actor cumulative damage per window
      "MaxHeadshotsPerWindow": 8,         // per-actor headshot rate (see HeadshotFlagOffset)
      // Byte offset of the headshot flag inside the game's DamagePacket custom type. The layout is
      // game-specific; until you set this from a local capture, headshot-rate checking is inert
      // (the other rate checks still run). A non-zero byte at the offset = headshot. (Black Ice's
      // DamagePacket carries Crit=bit0 / WeakPoint=bit1 in byte 39, so 39 catches weak-point hits.)
      "HeadshotFlagOffset": null
    },

    // Playerbot soak / anti-cheat exercise (off by default). With AutoSpawnPerRealm > 0 the server
    // spawns that many synthetic players into each realm on startup; with EmitGameActions true they
    // also drive a rotating script of legitimate AND cheating gameplay (chat, damage, equip, hacking,
    // npc spawns, loot, XP, plus teleport/over-max-damage/headshot-flood/view-spoof/event-flood) through
    // the relay, so the authority validators get exercised. Great for load/soak and anti-cheat tuning.
    "Bots": { "AutoSpawnPerRealm": 0, "EmitGameActions": false }
  },

  "Database": {
    // "Sqlite" (default) or "MySql".
    "Provider": "Sqlite",

    // SQLite: a relative Data Source is anchored next to the executable. MySQL: a standard
    // Pomelo/MySqlConnector connection string.
    "ConnectionString": "Data Source=blackice.db",

    // When true (default), the schema is brought up to date on startup: SQLite applies the
    // committed EF Core migrations; MySQL uses EnsureCreated. Set false to manage the schema out
    // of band (e.g. `dotnet ef database update` as a deploy step).
    "AutoMigrate": true
  },

  // Realms seeded into the database on first run (only when the Realms table is empty). After the
  // first run the database is authoritative; edit realms live with the `realmmotd` console command
  // or directly in the DB.
  "Realms": [
    { "Name": "Black Ice — Co-op", "DisplayName": "Co-op", "Pvp": false, "MaxPlayers": 8 },
    { "Name": "Black Ice — PvP", "DisplayName": "PvP", "Pvp": true, "MaxPlayers": 6 },
    { "Name": "Black Ice — Hardcore", "DisplayName": "Hardcore", "HackDifficultyIncrease": 5, "MaxPlayers": 4 }
  ]
}
```

## Environment overrides

Any value can be overridden by a `BLACKICE_`-prefixed environment variable, using `__` (double
underscore) to descend into nested objects. The override layer wins over the JSON file, so a
container can set secrets and connection strings without baking them into an image:

| Setting | Environment variable | Example |
|---|---|---|
| Advertised host | `BLACKICE_AdvertisedHost` | `203.0.113.7` |
| Token secret | `BLACKICE_Server__Secret` | `a-real-deployment-secret` |
| Game Server port | `BLACKICE_Server__Ports__GameServer` | `6056` |
| Database provider | `BLACKICE_Database__Provider` | `MySql` |
| Connection string | `BLACKICE_Database__ConnectionString` | `Server=db;Database=blackice;User=...` |
| Log level | `BLACKICE_LOG` | `Debug` |

Quick launch overrides also exist: the first command-line argument sets `AdvertisedHost`,
`--require-token` forces `AllowAnonymousLan=false`, and `--trace` / `--debug` set the log level.

## Schema migrations

The SQLite schema is managed with EF Core migrations (committed under
`server/BlackIce.Server.Data/Migrations`). To evolve it:

```bash
dotnet ef migrations add <Name> --project server/BlackIce.Server.Data
dotnet ef database update      --project server/BlackIce.Server.Data   # or let AutoMigrate apply on startup
```
