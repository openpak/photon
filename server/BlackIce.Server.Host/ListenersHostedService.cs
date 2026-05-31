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

    // NOTE: registry is resolved from DI; it is constructed with the configured AnticheatOptions in Program.
    public ListenersHostedService(ServerConfig config, IDbContextFactory<BlackIceDbContext> dbf,
                                  RoomRegistry registry, BotManager bots, AdminActionQueue admin)
    {
        _config = config;
        _dbf = dbf;
        _registry = registry;
        _bots = bots;
        _admin = admin;
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

        Log.Info("HOST", $"Listening — NS {s.Ports.NameServer} / Master {s.Ports.MasterServer} / Game {s.Ports.GameServer}");
        try
        {
            await Task.WhenAll(name.RunAsync(stoppingToken), master.RunAsync(stoppingToken), game.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
    }
}
