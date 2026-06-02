using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using BlackIce.Server.LoadBalancing.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BlackIce.Server.Host;

/// <summary>
/// Runs the three Photon role listeners (Name / Master / Game) for the lifetime of the host. Each
/// listener owns one loop thread; the data services they share (accounts/realms/motd) are built once
/// here from the pooled context factory, matching the single-graph wiring the manual bootstrap used.
/// Playerbot ticking is hung off the Game listener's maintenance pass so it stays on that one thread.
/// </summary>
public sealed class ListenersHostedService : BackgroundService
{
    private readonly ServerConfig _config;
    private readonly IDbContextFactory<BlackIceDbContext> _dbf;
    private readonly RoomRegistry _registry;
    private readonly BotManager _bots;
    private readonly AdminActionQueue _admin;
    private readonly BotIdentityGenerator _botIds;
    private readonly PluginManager _plugins;
    private readonly BlackIce.Server.LoadBalancing.Authority.RoomWorldStateRegistry _worlds;
    private readonly BlackIce.Server.LoadBalancing.Navigation.NavMeshRegistry _navs;
    private readonly BlackIce.Server.LoadBalancing.Auth.ISteamTicketValidator _steam;

    // NOTE: registry's interceptors come from the plugin manager (anti-cheat/game-mode logic lives in plugins).
    public ListenersHostedService(ServerConfig config, IDbContextFactory<BlackIceDbContext> dbf,
                                  RoomRegistry registry, BotManager bots, AdminActionQueue admin,
                                  BotIdentityGenerator botIds, PluginManager plugins,
                                  BlackIce.Server.LoadBalancing.Authority.RoomWorldStateRegistry worlds,
                                  BlackIce.Server.LoadBalancing.Navigation.NavMeshRegistry navs,
                                  BlackIce.Server.LoadBalancing.Auth.ISteamTicketValidator steam)
    {
        _steam = steam;
        _config = config;
        _dbf = dbf;
        _registry = registry;
        _bots = bots;
        _admin = admin;
        _botIds = botIds;
        _plugins = plugins;
        _worlds = worlds;
        _navs = navs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var accounts = new AccountService(_dbf.CreateDbContext());
        var realms = new RealmService(_dbf.CreateDbContext());
        var motd = new MotdService(_dbf.CreateDbContext());

        var s = _config.Server;
        // Steam ticket validation completes asynchronously on a Steam callback thread; its auth response must
        // be sent on the NameServer listener's single thread. Marshal it via this queue, drained in the
        // listener's OnMaintenance (the same thread-affinity pattern as the Game listener's admin queue).
        var nameQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var name = new UdpListener("NameServer", s.Ports.NameServer,
            new NameServerHandler($"{_config.AdvertisedHost}:{s.Ports.MasterServer}", s.Secret, accounts,
                                  _steam, _config.AllowAnonymousLan,
                                  isLan: null, post: nameQueue.Enqueue), s.Listener);
        name.OnMaintenance = () => { while (nameQueue.TryDequeue(out var a)) a(); };
        // Advertise bots as players in the lobby browser only when the operator opted in.
        Func<string, int>? lobbyBotCount = s.Bots.CountInLobby ? _bots.CountIn : null;
        var master = new UdpListener("MasterServer", s.Ports.MasterServer,
            new MasterServerHandler($"{_config.AdvertisedHost}:{s.Ports.GameServer}", s.Secret, _registry,
                                    _config.AllowAnonymousLan, accounts, realms, lobbyBotCount), s.Listener);
        // The in-game "/command" surface: the same commands as the console, runnable from chat but gated by
        // each player's VERIFIED level (GameServerHandler computes it; Console-tier stays console-only). Built
        // on the Game listener's services so its providers run on that thread (where chat is handled). Plugin
        // providers are shared with the console registry — they use thread-safe singletons + the admin queue.
        var chatCommands = new BlackIce.Server.Data.CommandRegistry()
            .Register(new ServerCommands(_registry, _admin, _bots, _botIds))
            .Register(new BlackIce.Server.Data.AccountCommands(accounts))
            .Register(new BlackIce.Server.Data.RealmCommands(realms))
            .Register(new BlackIce.Server.Data.MotdCommands(motd));
        foreach (var provider in _plugins.CommandProviders) chatCommands.Register(provider);
        var game = new UdpListener("GameServer", s.Ports.GameServer,
            new GameServerHandler(s.Secret, _registry, _config.AllowAnonymousLan, accounts, realms, motd, _plugins, chatCommands), s.Listener);

        // Tick bots AND drain queued admin actions on the Game listener's single thread: both relay to
        // peers, mutating the same EnetPeer send state that thread already owns, so neither may run
        // anywhere else.
        game.OnMaintenance = () => { _bots.Tick(); _admin.Drain(); };

        // Optional playerbot soak: auto-spawn N bots per realm and (if configured) have them drive the
        // legitimate+cheating game-action script so the relay and anti-cheat surface get exercised.
        if (s.Bots.AutoSpawnPerRealm > 0)
        {
            _bots.EmitGameActions = s.Bots.EmitGameActions;
            _bots.Smart = s.Bots.Smart;      // world-aware hunting bots (read the shared world-state)
            _bots.Worlds = _worlds;          // same per-room shadow the authority plugin writes
            _bots.Navs = _navs;              // walkable navmeshes the hunters path on (null per-room → fallback)
            _bots.Maps = new BlackIce.Server.LoadBalancing.Navigation.MapSelector(_navs);  // auto-detect each room's map from live player positions (no client map id)
            _bots.Walkable = new BlackIce.Server.LoadBalancing.Navigation.WalkableMapRegistry(_navs.MapsDirectory);  // learn the walkable surface from real players' movement (procedural world: no static navmesh)
            _bots.Modes = _registry.Modes;   // so soak bots get team-assigned in team-mode realms
            // Realm → navmesh map name, read from each realm's ExtraJson {"navmesh":"level13"}. Only realms that
            // declare one (and whose maps/<name>.navmesh exists) get surface-aware bots; the rest fall back.
            var roomMaps = new Dictionary<string, string>();
            foreach (var realm in _config.Realms)
            {
                _registry.GetOrCreate(realm.Name);
                _registry.Modes.SetMode(realm.Name, GameModeRegistry.Parse(realm.Mode));   // record the mode so bots are assigned + damage is filtered
                if (NavmeshMapName(realm.ExtraJson) is { } mapName) roomMaps[realm.Name] = mapName;
                var session = _registry.Session(realm.Name);
                for (int n = 0; n < s.Bots.AutoSpawnPerRealm; n++) _bots.RequestSpawn(session, _botIds.Next());
            }
            _bots.RoomMaps = roomMaps;
            int withMesh = roomMaps.Count;
            Log.Info("HOST", $"bot soak: {s.Bots.AutoSpawnPerRealm}/realm, gameActions={s.Bots.EmitGameActions}, " +
                             $"map auto-detect from {_bots.Maps.CandidateCount} candidate map(s) (maps dir {_navs.MapsDirectory})" +
                             (withMesh > 0 ? $", pinned realms={withMesh}" : ""));
        }

        Log.Info("HOST", $"Listening — NS {s.Ports.NameServer} / Master {s.Ports.MasterServer} / Game {s.Ports.GameServer}");
        try
        {
            await Task.WhenAll(name.RunAsync(stoppingToken), master.RunAsync(stoppingToken), game.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
    }

    /// <summary>
    /// Reads the navmesh map name from a realm's <c>ExtraJson</c> — the value of a top-level <c>"navmesh"</c>
    /// string property (e.g. <c>{"navmesh":"level13"}</c>) — or null if absent/blank/unparseable. This is the
    /// realm→map association: a realm that names a map gets surface-aware bots (when the artifact exists);
    /// every other realm has no navmesh and keeps today's player-anchor movement. Tolerant of malformed
    /// ExtraJson (returns null) so a bad knob never crashes startup.
    /// </summary>
    internal static string? NavmeshMapName(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(extraJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("navmesh", out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = v.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (System.Text.Json.JsonException) { /* malformed ExtraJson — treat as no navmesh */ }
        return null;
    }
}
