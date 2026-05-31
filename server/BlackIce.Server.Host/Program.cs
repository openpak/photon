using BlackIce.Server.Common;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.Host;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Configuration: blackice.server.json (generated with defaults if absent) layered under BLACKICE_*
// environment overrides. The positional arg and --require-token flag remain as quick launch overrides.
var config = ServerConfig.Load("blackice.server.json");
if (args.Length > 0 && !args[0].StartsWith("--")) config.AdvertisedHost = args[0];
if (args.Contains("--require-token")) config.AllowAnonymousLan = false;

// Diagnostics: leveled file+console log (the static Log; migration to ILogger is a separate step).
Log.Level = ResolveLogLevel(args);
var logPath = Path.Combine(AppContext.BaseDirectory, $"blackice-server-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Log.ToFile(logPath);
// Route the Result handling policies' diagnostics (LogAndDrop / Expect) into the server log.
ResultDiagnostics.Warn = Log.Warn;
ResultDiagnostics.Error = Log.Error;
RegisterCrashHandlers();

// Fail fast on a misconfiguration rather than half-starting and failing obscurely once a client connects.
var configErrors = config.Server.Validate();
if (configErrors.Count > 0)
{
    foreach (var e in configErrors) Log.Error("HOST", $"config error: {e}");
    Log.Flush();
    return 1;
}
if (config.Server.UsesDefaultSecret)
    Log.Warn("HOST", "Server.Secret is the shipped default — change it in blackice.server.json before exposing the server publicly.");

// Generic Host: dependency injection + lifetime (Ctrl-C / SIGTERM graceful shutdown) for the listeners
// and the console. We don't hand our custom args to the host's command-line config (they aren't
// switch-mapped), so the host builder is created without them.
var builder = Host.CreateApplicationBuilder();
// The server's own diagnostics go through the static Log (file + console). Keep the host's ILogger
// pipeline quiet except for warnings so EF Core's per-statement SQL chatter doesn't double the output.
// Fully qualified to avoid clashing with Core's own LogLevel enum that is in scope here.
Microsoft.Extensions.Logging.FilterLoggingBuilderExtensions.AddFilter(
    builder.Logging, "Microsoft", Microsoft.Extensions.Logging.LogLevel.Warning);
builder.Services.AddSingleton(config);
builder.Services.AddDbContextFactory<BlackIceDbContext>(
    (sp, b) => sp.GetRequiredService<ServerConfig>().Database.Configure(b));
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<BotManager>();
builder.Services.AddSingleton<BotIdentityGenerator>();
builder.Services.AddHostedService<ListenersHostedService>();
builder.Services.AddHostedService<ConsoleHostedService>();

var host = builder.Build();

// One-time startup (schema, bootstrap code, realm seeding, config MOTDs) before listeners go live.
StartupInitializer.Run(host.Services, config, logPath);

await host.RunAsync();
Log.Info("HOST", "BlackIce.Server stopped.");
Log.Flush();
return 0;

static LogLevel ResolveLogLevel(string[] args)
{
    if (args.Contains("--trace")) return LogLevel.Trace;
    if (args.Contains("--debug")) return LogLevel.Debug;
    var env = Environment.GetEnvironmentVariable("BLACKICE_LOG");
    return Enum.TryParse<LogLevel>(env, ignoreCase: true, out var lvl) ? lvl : LogLevel.Info;
}

static void RegisterCrashHandlers()
{
    // Make crashes loud and recorded. An unhandled exception on any thread, or a faulted Task nobody
    // awaited, would otherwise kill the process silently — which looks exactly like the client
    // "getting kicked" (the server stops replying and the client times out).
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Log.Error("FATAL", $"Unhandled exception (terminating={e.IsTerminating}): " +
                           $"{(e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString()}");
        Log.Flush();
    };
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Log.Exception("FATAL", "Unobserved task exception", e.Exception);
        e.SetObserved();
        Log.Flush();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { Log.Info("HOST", "process exiting"); Log.Flush(); };
}
