using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
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

    // NOTE: registry is resolved from DI; it is constructed with the configured AnticheatOptions in Program.
    public ListenersHostedService(ServerConfig config, IDbContextFactory<BlackIceDbContext> dbf,
                                  RoomRegistry registry, BotManager bots, AdminActionQueue admin, BotIdentityGenerator botIds)
    {
        _config = config;
        _dbf = dbf;
        _registry = registry;
        _bots = bots;
        _admin = admin;
        _botIds = botIds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var accounts = new AccountService(_dbf.CreateDbContext());
        var realms = new RealmService(_dbf.CreateDbContext());
        var motd = new MotdService(_dbf.CreateDbContext());

        var s = _config.Server;
        var name = new UdpListener("NameServer", s.Ports.NameServer,
            new NameServerHandler($"{_config.AdvertisedHost}:{s.Ports.MasterServer}", s.Secret, accounts), s.Listener);
        var master = new UdpListener("MasterServer", s.Ports.MasterServer,
            new MasterServerHandler($"{_config.AdvertisedHost}:{s.Ports.GameServer}", s.Secret, _registry,
                                    _config.AllowAnonymousLan, accounts, realms), s.Listener);
        var game = new UdpListener("GameServer", s.Ports.GameServer,
            new GameServerHandler(s.Secret, _registry, _config.AllowAnonymousLan, accounts, realms, motd), s.Listener);

        // Tick bots AND drain queued admin actions on the Game listener's single thread: both relay to
        // peers, mutating the same EnetPeer send state that thread already owns, so neither may run
        // anywhere else.
        game.OnMaintenance = () => { _bots.Tick(); _admin.Drain(); };

        // Optional playerbot soak: auto-spawn N bots per realm and (if configured) have them drive the
        // legitimate+cheating game-action script so the relay and anti-cheat surface get exercised.
        if (s.Bots.AutoSpawnPerRealm > 0)
        {
            _bots.EmitGameActions = s.Bots.EmitGameActions;
            _bots.Modes = _registry.Modes;   // so soak bots get team-assigned in team-mode realms
            foreach (var realm in _config.Realms)
            {
                _registry.GetOrCreate(realm.Name);
                _registry.Modes.SetMode(realm.Name, GameModeRegistry.Parse(realm.Mode));   // record the mode so bots are assigned + damage is filtered
                var session = _registry.Session(realm.Name);
                for (int n = 0; n < s.Bots.AutoSpawnPerRealm; n++) _bots.RequestSpawn(session, _botIds.Next());
            }
            Log.Info("HOST", $"bot soak: {s.Bots.AutoSpawnPerRealm}/realm, gameActions={s.Bots.EmitGameActions}");
        }

        Log.Info("HOST", $"Listening — NS {s.Ports.NameServer} / Master {s.Ports.MasterServer} / Game {s.Ports.GameServer}");
        try
        {
            await Task.WhenAll(name.RunAsync(stoppingToken), master.RunAsync(stoppingToken), game.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
    }
}
