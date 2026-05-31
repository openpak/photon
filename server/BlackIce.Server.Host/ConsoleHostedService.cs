using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BlackIce.Server.Host;

/// <summary>
/// Runs the admin console loop on its own background thread, with its own data context so it never
/// shares a DbContext with the listener threads. Commands are dispatched to ConsoleCommandProcessor;
/// 'bot &lt;realm&gt;' is special-cased to enqueue a spawn that the Game listener performs on its own
/// thread (BotManager.RequestSpawn), since spawning relays to every real peer's transport state.
/// </summary>
public sealed class ConsoleHostedService : BackgroundService
{
    private readonly IDbContextFactory<BlackIceDbContext> _dbf;
    private readonly RoomRegistry _registry;
    private readonly BotManager _bots;
    private readonly BotIdentityGenerator _botIdentities;

    public ConsoleHostedService(IDbContextFactory<BlackIceDbContext> dbf, RoomRegistry registry,
                                BotManager bots, BotIdentityGenerator botIdentities)
    {
        _dbf = dbf;
        _registry = registry;
        _bots = bots;
        _botIdentities = botIdentities;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = new ConsoleCommandProcessor(new AccountService(_dbf.CreateDbContext()),
                                                    new MotdService(_dbf.CreateDbContext()));
        // Console.ReadLine blocks; run it on a dedicated background thread so it never holds up host
        // shutdown (the thread is reclaimed when the process exits, as before).
        var thread = new Thread(() => Loop(processor, stoppingToken)) { IsBackground = true, Name = "console" };
        thread.Start();
        return Task.CompletedTask;
    }

    private void Loop(ConsoleCommandProcessor processor, CancellationToken stoppingToken)
    {
        Console.WriteLine("console ready — type 'help'.");
        string? line;
        while (!stoppingToken.IsCancellationRequested && (line = Console.ReadLine()) != null)
        {
            if (line.StartsWith("bot ", StringComparison.OrdinalIgnoreCase))
            {
                var realmName = line.Substring(4).Trim();
                if (string.IsNullOrWhiteSpace(realmName)) { Console.WriteLine("usage: bot <realm>"); continue; }
                _registry.GetOrCreate(realmName);                 // ensure the room exists
                var session = _registry.Session(realmName);
                _bots.RequestSpawn(session, _botIdentities.Next());
                Console.WriteLine($"queued bot spawn for \"{realmName}\" (runs on next listener tick)");
                continue;
            }
            try
            {
                var output = processor.Execute(line);
                if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
            }
            catch (Exception ex) { Log.Exception("CONSOLE", $"command '{line}' failed", ex); }
        }
    }
}
