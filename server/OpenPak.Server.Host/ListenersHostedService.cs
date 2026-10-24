using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using OpenPak.Server.LoadBalancing.Bots;
using OpenPak.Server.LoadBalancing.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace OpenPak.Server.Host;

/// <summary>
/// Runs the three Photon role listeners (Name / Master / Game) for the lifetime of the host. Each
/// listener owns one loop thread; the data services they share (accounts/realms/motd) are built once
/// here from the pooled context factory, matching the single-graph wiring the manual bootstrap used.
/// Playerbot ticking is hung off the Game listener's maintenance pass so it stays on that one thread.
/// </summary>
public sealed class ListenersHostedService : BackgroundService
{
    private readonly ServerConfig _config;
    private readonly IDbContextFactory<OpenPakDbContext> _dbf;
    private readonly RoomRegistry _registry;
    private readonly BotManager _bots;
    private readonly AdminActionQueue _admin;
    private readonly BotIdentityGenerator _botIds;
    private readonly PluginManager _plugins;
    private readonly OpenPak.Server.LoadBalancing.Authority.RoomWorldStateRegistry _worlds;
    private readonly OpenPak.Server.LoadBalancing.Navigation.NavMeshRegistry _navs;
    private readonly OpenPak.Server.LoadBalancing.Auth.ISteamTicketValidator _steam;
    private readonly NodeDirectory _nodes;
    private readonly TokenKeys _keys;

    // NOTE: registry's interceptors come from the plugin manager (anti-cheat/game-mode logic lives in plugins).
    public ListenersHostedService(ServerConfig config, IDbContextFactory<OpenPakDbContext> dbf,
                                  RoomRegistry registry, BotManager bots, AdminActionQueue admin,
                                  BotIdentityGenerator botIds, PluginManager plugins,
                                  OpenPak.Server.LoadBalancing.Authority.RoomWorldStateRegistry worlds,
                                  OpenPak.Server.LoadBalancing.Navigation.NavMeshRegistry navs,
                                  OpenPak.Server.LoadBalancing.Auth.ISteamTicketValidator steam,
                                  NodeDirectory nodes, TokenKeys keys)
    {
        _keys = keys;
        _steam = steam;
        _nodes = nodes;
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
        var accountIdentities = new AccountIdentityRegistry();
        // Room presence goes to the Switch adapter (nx-baas) now, not the account server: a
        // deliberately new variable name, so a rig still setting the old account-server URL stops
        // publishing rather than posting this shape at the wrong service.
        var accountPublisher = new AccountRoomPublisher(new HttpClient(),
                                                          Env("OPENPAK_BAAS_URL"),
                                                          Env("OPENPAK_INTERNAL_KEY"),
                                                          Env("OPENPAK_TITLE_ID"),
                                                          PhotonTitles.Parse(Env("OPENPAK_PHOTON_TITLES")));
        // Steam ticket validation completes asynchronously on a Steam callback thread; its auth response must
        // be sent on the NameServer listener's single thread. Marshal it via this queue, drained in the
        // listener's OnMaintenance (the same thread-affinity pattern as the Game listener's admin queue).
        var nameQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var nameHandler = new NameServerHandler($"{_config.AdvertisedHost}:{s.Ports.MasterServer}", s.Secret, accounts,
                                  _steam, _config.AllowAnonymousLan,
                                  isLan: null, post: nameQueue.Enqueue,
                                  accountIdentities: accountIdentities, nodes: _nodes,
                                  loopbackMasterAddress: $"127.0.0.1:{s.Ports.MasterServerAlt ?? s.Ports.MasterServer}", keys: _keys);
        var name = new UdpListener("NameServer", s.Ports.NameServer, nameHandler, s.Listener);
        name.OnMaintenance = () => { while (nameQueue.TryDequeue(out var a)) a(); };
        // See ServerPorts.NameServerAlt: some clients try this port independently of GetRegions.
        var nameAlt = s.Ports.NameServerAlt is { } altPort
            ? new UdpListener("NameServerAlt", altPort, nameHandler, s.Listener)
            : null;
        if (nameAlt != null) nameAlt.OnMaintenance = name.OnMaintenance;
        // Name Server over WebSocket (Photon Realtime C# AuthOnceWss: wss://ns.photonengine.io:443, TLS ended in
        // front). Off unless OPENPAK_PHOTON_NS_WS_PORT is set.
        var nameWs = int.TryParse(Env("OPENPAK_PHOTON_NS_WS_PORT"), out var wsPort) && wsPort > 0
            ? new WebSocketListener("NameServerWs", wsPort, nameHandler, new object())
            : null;
        // Advertise bots as players in the lobby browser only when the operator opted in.
        Func<string, int>? lobbyBotCount = s.Bots.CountInLobby ? _bots.CountIn : null;
        var masterHandler = new MasterServerHandler($"{_config.AdvertisedHost}:{s.Ports.GameServer}", s.Secret, _registry,
                                    _config.AllowAnonymousLan, accounts, realms, lobbyBotCount, _nodes, _keys);
        var master = new UdpListener("MasterServer", s.Ports.MasterServer, masterHandler, s.Listener);
        // See ServerPorts.NameServerAlt: pairs with NameServerAlt/GameServerAlt for a client using
        // Photon's modern default UDP ports (27000/27001/27002).
        var masterAlt = s.Ports.MasterServerAlt is { } masterAltPort
            ? new UdpListener("MasterServerAlt", masterAltPort, masterHandler, s.Listener)
            : null;
        // The in-game "/command" surface: the same commands as the console, runnable from chat but gated by
        // each player's VERIFIED level (GameServerHandler computes it; Console-tier stays console-only). Built
        // on the Game listener's services so its providers run on that thread (where chat is handled). Plugin
        // providers are shared with the console registry — they use thread-safe singletons + the admin queue.
        var chatCommands = new OpenPak.Server.Data.CommandRegistry()
            .Register(new ServerCommands(_registry, _admin, _bots, _botIds))
            .Register(new OpenPak.Server.Data.AccountCommands(accounts))
            .Register(new OpenPak.Server.Data.RealmCommands(realms))
            .Register(new OpenPak.Server.Data.MotdCommands(motd));
        foreach (var provider in _plugins.CommandProviders) chatCommands.Register(provider);
        // Host Fusion 2's Shared-mode simulation per room (the role Photon Cloud's Fusion plugin plays), so
        // two+ Fusion peers relay real gameplay. Its relay datagrams go out as event 101 (server sender,
        // actor 0) to the addressed actor on the room's own listener loop: unreliable, except fragment
        // pieces and anything too big for one unreliable Photon datagram (see FusionSharedHost).
        // A room's members can sit on different listeners (GameServer vs GameServerAlt), so this callback,
        // run on the room's own loop, may raise on a peer owned by the other loop — as RoomSession.RelayFrom
        // already does for every relayed event. That is safe: a peer's outgoing sequence state is guarded
        // by EnetPeer's lock, the server keeps no resend state, and the send is one UdpClient.Send on that
        // peer's own socket. What two loops can do is interleave two sends to one peer; the client orders
        // reliable commands itself and the notify layer drops a stale unreliable datagram as lost.
        IFusionSessionHost fusion = new OpenPak.Server.LoadBalancing.Fusion.FusionSharedHost(
            (room, actor, payload, reliable) =>
                _registry.Session(room).SendToActor(actor, new EventData(101, new() { { (byte)254, 0 }, { (byte)245, payload } }), unreliable: !reliable),
            new OpenPak.Server.LoadBalancing.Fusion.FusionRoomOptions { SceneAuthorityMasterClient = s.Fusion.SceneAuthorityMasterClient });

        // Photon Quantum 2 rooms (Stumble Guys): the deterministic session's server half. Its events go out
        // from the server (sender 0) to one actor, on the room's own loop like Fusion's.
        var quantum = new OpenPak.Server.LoadBalancing.Quantum.QuantumHost(
            (room, actor, code, payload, reliable) =>
                _registry.Session(room).SendToActor(actor, new EventData(code, new() { { (byte)254, 0 }, { (byte)245, payload } }), unreliable: !reliable));
        var quantumAppIds = (Env("OPENPAK_QUANTUM_APPIDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A region node has no adapter to publish presence to; home does that from the room report.
        var fed = _config.Federation;
        RoomReporter? reporter = fed.IsNode ? new RoomReporter(new HttpClient(), fed.HomeUrl, fed.ReportKey, fed.NodeId) : null;
        var gameHandler = new GameServerHandler(s.Secret, _registry, _config.AllowAnonymousLan, accounts, realms, motd, _plugins,
                                  chatCommands, _config.TrustLanAdmin, (peer, room) =>
                                  {
                                      if (reporter is null && peer.SteamId is { } userId && accountIdentities.Find(userId) is { } bsDid)
                                          _ = accountPublisher.PublishAsync(bsDid, room, peer.AppId);
                                  }, fusion,
                                  roomChanged: reporter is null ? null : (room, creator) =>
                                  {
                                      // The creator's identity travels with the first report so home can
                                      // turn the room into that player's friend presence.
                                      string? identity = creator?.SteamId is { } uid ? accountIdentities.Find(uid) : null;
                                      _ = reporter.ReportAsync(room, identity);
                                  }, keys: _keys, quantum: quantum, quantumAppIds: quantumAppIds);
        var game = new UdpListener("GameServer", s.Ports.GameServer, gameHandler, s.Listener);
        // See ServerPorts.NameServerAlt: pairs with NameServerAlt/MasterServerAlt for a client using
        // Photon's modern default UDP ports (27000/27001/27002).
        var gameAlt = s.Ports.GameServerAlt is { } gameAltPort
            ? new UdpListener("GameServerAlt", gameAltPort, gameHandler, s.Listener)
            : null;

        // Tick bots AND drain queued admin actions on the Game listener's single thread: both relay to
        // peers, mutating the same EnetPeer send state that thread already owns, so neither may run
        // anywhere else.
        game.OnMaintenance = () => { _bots.Tick(); _admin.Drain(); };
        // Advance hosted Fusion room simulations on the Game listener loop(s). Each room only ticks from the
        // loop that created it (UdpListener.Current, not a thread id — the loop hops pool threads), so
        // wiring both game and gameAlt is safe.
        game.OnTick = () => { fusion.Tick(); quantum.Tick(); };
        if (gameAlt != null) gameAlt.OnTick = game.OnTick;
        if (masterAlt != null) masterAlt.OnMaintenance = master.OnMaintenance;
        if (gameAlt != null) gameAlt.OnMaintenance = game.OnMaintenance;

        // Optional playerbot soak: auto-spawn N bots per realm and (if configured) have them drive the
        // legitimate+cheating game-action script so the relay and anti-cheat surface get exercised.
        if (s.Bots.AutoSpawnPerRealm > 0)
        {
            _bots.EmitGameActions = s.Bots.EmitGameActions;
            _bots.Smart = s.Bots.Smart;      // world-aware hunting bots (read the shared world-state)
            _bots.Worlds = _worlds;          // same per-room shadow the authority plugin writes
            _bots.Navs = _navs;              // walkable navmeshes the hunters path on (null per-room → fallback)
            _bots.Maps = new OpenPak.Server.LoadBalancing.Navigation.MapSelector(_navs);  // auto-detect each room's map from live player positions (no client map id)
            _bots.Walkable = new OpenPak.Server.LoadBalancing.Navigation.WalkableMapRegistry(_navs.MapsDirectory);  // learn the walkable surface from real players' movement (procedural world: no static navmesh)
            _bots.Modes = _registry.Modes;   // so soak bots get team-assigned in team-mode realms
            // Realm → navmesh map name, read from each realm's ExtraJson {"navmesh":"level13"}. Only realms that
            // declare one (and whose maps/<name>.navmesh exists) get surface-aware bots; the rest fall back.
            var roomMaps = new Dictionary<string, string>();
            foreach (var realm in _config.Realms)
            {
                // Soak bots belong to no game build: the realm's room for clients that send no AppId/AppVersion.
                var key = RoomRegistry.KeyFor(null, null, realm.Name);
                _registry.GetOrCreate(key);
                _registry.Modes.SetMode(key, GameModeRegistry.Parse(realm.Mode));   // record the mode so bots are assigned + damage is filtered
                if (NavmeshMapName(realm.ExtraJson) is { } mapName) roomMaps[key] = mapName;
                var session = _registry.Session(key);
                for (int n = 0; n < s.Bots.AutoSpawnPerRealm; n++) _bots.RequestSpawn(session, _botIds.Next());
            }
            _bots.RoomMaps = roomMaps;
            int withMesh = roomMaps.Count;
            Log.Info("HOST", $"bot soak: {s.Bots.AutoSpawnPerRealm}/realm, gameActions={s.Bots.EmitGameActions}, " +
                             $"map auto-detect from {_bots.Maps.CandidateCount} candidate map(s) (maps dir {_navs.MapsDirectory})" +
                             (withMesh > 0 ? $", pinned realms={withMesh}" : ""));
        }

        if (fed.IsNode)
        {
            // Game role only: matchmaking is home's. Name/Master listeners never open here.
            Log.Info("HOST", $"Region node {fed.NodeId}: Game {s.Ports.GameServer}{(gameAlt != null ? $"/{s.Ports.GameServerAlt}" : "")}, reporting to {Redact.Url(fed.HomeUrl)}");
            try
            {
                var nodeTasks = new List<Task> { game.RunAsync(stoppingToken) };
                if (gameAlt != null) nodeTasks.Add(gameAlt.RunAsync(stoppingToken));
                // Clients rate a region by pinging UDP 5055 of its host; answer there unless a listener does.
                if (fed.PingPort > 0 && fed.PingPort != s.Ports.GameServer && fed.PingPort != s.Ports.GameServerAlt)
                    nodeTasks.Add(RegionPing.RunResponderAsync(fed.PingPort, stoppingToken));
                await Task.WhenAll(nodeTasks);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
            return;
        }

        Log.Info("HOST", $"Listening — NS {s.Ports.NameServer}{(nameAlt != null ? $"/{s.Ports.NameServerAlt}" : "")} / " +
                         $"Master {s.Ports.MasterServer}{(masterAlt != null ? $"/{s.Ports.MasterServerAlt}" : "")} / " +
                         $"Game {s.Ports.GameServer}{(gameAlt != null ? $"/{s.Ports.GameServerAlt}" : "")}");
        try
        {
            var tasks = new List<Task> { name.RunAsync(stoppingToken), master.RunAsync(stoppingToken), game.RunAsync(stoppingToken) };
            if (nameAlt != null) tasks.Add(nameAlt.RunAsync(stoppingToken));
            if (nameWs != null) tasks.Add(nameWs.RunAsync(stoppingToken));
            if (masterAlt != null) tasks.Add(masterAlt.RunAsync(stoppingToken));
            if (gameAlt != null) tasks.Add(gameAlt.RunAsync(stoppingToken));
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
    }

    private static string? Env(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));

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
