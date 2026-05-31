using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.Host;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;

var config = ServerConfig.Load("blackice.server.json");
if (args.Length > 0 && !args[0].StartsWith("--")) config.AdvertisedHost = args[0];
if (args.Contains("--require-token")) config.AllowAnonymousLan = false;
const string secret = "change-me-platform-sp1";

// --- Diagnostics ----------------------------------------------------------------------------
// Log level: --trace / --debug (or env BLACKICE_LOG=Trace|Debug|Info|Warn|Error). Default Info.
// Every run also appends-or-replaces a timestamped log file next to the exe for offline analysis.
Log.Level = ResolveLogLevel(args);
var logPath = Path.Combine(AppContext.BaseDirectory, $"blackice-server-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Log.ToFile(logPath);

// Make crashes loud and recorded. An unhandled exception on any thread, or a faulted Task that
// nobody awaited, would otherwise kill the process silently — which looks exactly like the
// client "getting kicked" (the server stops replying and the client times out).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Error("FATAL", $"Unhandled exception (terminating={e.IsTerminating}): {(e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString()}");
    Log.Flush();
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Exception("FATAL", "Unobserved task exception", e.Exception);
    e.SetObserved();
    Log.Flush();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => { Log.Info("HOST", "process exiting"); Log.Flush(); };

using var db = config.Database.CreateContext();      // EnsureCreated runs here
var accounts = new AccountService(db);

Log.Info("HOST", $"BlackIce.Server starting — DB {config.Database.Provider}, advertising {config.AdvertisedHost}, " +
                 $"anonLan={config.AllowAnonymousLan}, logLevel={Log.Level}");
Log.Info("HOST", $"log file: {logPath}");
Log.Info("HOST", $"One-time bootstrap code: {accounts.EnsureBootstrapCode()}");

var realms = new RealmService(config.Database.CreateContext());
var motd = new MotdService(config.Database.CreateContext());
realms.SeedDefaults(config.Realms);

// Apply MOTDs from config on startup, reusing the same service the console commands use. Each is
// guarded by a non-empty check so an absent config value leaves any live `motd`/`realmmotd` edit
// intact (config is authoritative only when it actually specifies a value). SetRealm needs the
// realm to exist, which SeedDefaults has just ensured.
if (!string.IsNullOrWhiteSpace(config.Motd))
{
    motd.SetGlobal(config.Motd);
    Log.Info("HOST", $"global MOTD from config: \"{config.Motd}\"");
}
foreach (var r in config.Realms.Where(r => !string.IsNullOrWhiteSpace(r.Motd)))
    if (motd.SetRealm(r.Name, r.Motd)) Log.Info("HOST", $"realm MOTD from config: {r.Name} -> \"{r.Motd}\"");

var registry = new RoomRegistry();
var botManager = new BotManager();
var botIdentities = new BotIdentityGenerator();
Log.Info("HOST", $"Realms: {string.Join(", ", realms.ListVisible().Select(r => r.Name))}");

// The GameServer listener is held as a local so we can hook its maintenance pass. Ticking bots
// here (rather than on a Timer/Task) keeps the bot relay path on the listener's single thread —
// critical, since BotManager.Tick -> RelayFrom mutates the same EnetPeer send state this thread
// already owns. NOTE: 1 Hz (maintenance cadence) gives coarse movement; finer cadence is a future tweak.
var nameListener = new UdpListener("NameServer", 5058, new NameServerHandler($"{config.AdvertisedHost}:5055", secret, accounts));
var masterListener = new UdpListener("MasterServer", 5055, new MasterServerHandler($"{config.AdvertisedHost}:5056", secret, registry, config.AllowAnonymousLan, accounts, realms));
var gameListener = new UdpListener("GameServer", 5056, new GameServerHandler(secret, registry, config.AllowAnonymousLan, accounts, realms, motd));
gameListener.OnMaintenance = () => botManager.Tick();
var listeners = new[] { nameListener, masterListener, gameListener };

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Console admin command loop on a background thread (its own context for thread-safety).
var processor = new ConsoleCommandProcessor(new AccountService(config.Database.CreateContext()),
                                            new MotdService(config.Database.CreateContext()));
var consoleThread = new Thread(() =>
{
    Console.WriteLine("console ready — type 'help'.");
    string? line;
    while (!cts.IsCancellationRequested && (line = Console.ReadLine()) != null)
    {
        // 'bot <realm>' queues a playerbot spawn into a realm. We must NOT spawn on this console
        // thread: BotManager.Spawn mutates _bots and relays the bot's join/instantiate to every real
        // peer (touching their EnetPeer sequence state), which only the GameServer listener thread
        // may do. RequestSpawn enqueues; the actual spawn (and its relay) runs on the next listener
        // maintenance tick. The actor number isn't known synchronously anymore — it's logged when the
        // spawn runs on the listener thread.
        if (line.StartsWith("bot ", StringComparison.OrdinalIgnoreCase))
        {
            var realmName = line.Substring(4).Trim();
            registry.GetOrCreate(realmName);                 // ensure the room exists
            var session = registry.Session(realmName);
            botManager.RequestSpawn(session, botIdentities.Next());
            Console.WriteLine($"queued bot spawn for \"{realmName}\" (runs on next listener tick)");
            continue;
        }
        try
        {
            var outp = processor.Execute(line);
            if (!string.IsNullOrEmpty(outp)) Console.WriteLine(outp);
        }
        catch (Exception ex) { Log.Exception("CONSOLE", $"command '{line}' failed", ex); }
    }
}) { IsBackground = true };
consoleThread.Start();

Log.Info("HOST", "Listening — NS 5058 / Master 5055 / Game 5056");
try { await Task.WhenAll(listeners.Select(l => l.RunAsync(cts.Token))); }
catch (OperationCanceledException) { }
catch (Exception ex) { Log.Exception("FATAL", "listener task faulted", ex); }
Log.Info("HOST", "BlackIce.Server stopped.");
Log.Flush();

static LogLevel ResolveLogLevel(string[] args)
{
    if (args.Contains("--trace")) return LogLevel.Trace;
    if (args.Contains("--debug")) return LogLevel.Debug;
    var env = Environment.GetEnvironmentVariable("BLACKICE_LOG");
    return Enum.TryParse<LogLevel>(env, ignoreCase: true, out var lvl) ? lvl : LogLevel.Info;
}
