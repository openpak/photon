using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using BlackIce.Server.LoadBalancing.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BlackIce.Server.Host;

/// <summary>
/// Runs the admin console loop on its own background thread, with its own data context so it never
/// shares a DbContext with the listener threads. It builds one <see cref="CommandRegistry"/> over all
/// the command providers — account moderation, MOTD, and live server/room/debug commands — and
/// dispatches at Console level (the local console has full authority). Commands that send packets queue
/// onto the Game listener thread via <see cref="AdminActionQueue"/>.
/// </summary>
public sealed class ConsoleHostedService : BackgroundService
{
    private readonly IDbContextFactory<BlackIceDbContext> _dbf;
    private readonly RoomRegistry _registry;
    private readonly AdminActionQueue _admin;
    private readonly BotManager _bots;
    private readonly BotIdentityGenerator _botIdentities;
    private readonly PluginManager _plugins;
    private readonly IServiceProvider _services;

    public ConsoleHostedService(IDbContextFactory<BlackIceDbContext> dbf, RoomRegistry registry,
                                AdminActionQueue admin, BotManager bots, BotIdentityGenerator botIdentities,
                                PluginManager plugins, IServiceProvider services)
    {
        _dbf = dbf;
        _registry = registry;
        _admin = admin;
        _bots = bots;
        _botIdentities = botIdentities;
        _plugins = plugins;
        _services = services;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var commands = new CommandRegistry()
            .Register(new AccountCommands(new AccountService(_dbf.CreateDbContext())))
            .Register(new MotdCommands(new MotdService(_dbf.CreateDbContext())))
            .Register(new RealmCommands(new RealmService(_dbf.CreateDbContext())))
            .Register(new ServerCommands(_registry, _admin, _bots, _botIdentities))
            .Register(new PluginCommands(_plugins, _services));
        // Console commands contributed by plugins.
        foreach (var provider in _plugins.CommandProviders) commands.Register(provider);

        // Console.ReadLine blocks; run it on a dedicated background thread so it never holds up host
        // shutdown (the thread is reclaimed when the process exits, as before).
        var thread = new Thread(() => Loop(commands, stoppingToken)) { IsBackground = true, Name = "console" };
        thread.Start();
        return Task.CompletedTask;
    }

    private static void Loop(CommandRegistry commands, CancellationToken stoppingToken)
    {
        Console.WriteLine("console ready — type 'help'.");
        string? line;
        while (!stoppingToken.IsCancellationRequested && (line = Console.ReadLine()) != null)
        {
            try
            {
                // The local console runs at the highest tier (Console); a future remote admin would pass
                // the authenticated account's PlayerLevel here instead.
                var output = commands.TryExecute(line, PlayerLevel.Console, out var result)
                    ? result
                    : $"unknown command. type 'help'.";
                if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
            }
            catch (Exception ex) { Log.Exception("CONSOLE", $"command '{line}' failed", ex); }
        }
    }
}
