using System.Diagnostics;

namespace BlackIce.Server.Core;

/// <summary>Diagnostic verbosity. TRACE logs every packet/byte; INFO is the normal operational level.</summary>
public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

/// <summary>
/// Minimal leveled logger with a console sink and an optional file sink. Thread-safe (the UDP
/// listeners run concurrently). Lines are timestamped to millisecond UTC and tagged with the
/// log level and a short category, so a captured file can be correlated with the client oplog.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();
    private static StreamWriter? _file;
    private static readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>Minimum level emitted. Set from config/env before serving; defaults to Info.</summary>
    public static LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>Opens (or replaces) the file sink. Safe to call once at startup.</summary>
    public static void ToFile(string path)
    {
        lock (_gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,   // crash-safety: we want every line on disk even if the process dies
            };
            _file.WriteLine($"# BlackIce.Server log — {DateTime.UtcNow:O}");
        }
    }

    public static void Flush() { lock (_gate) _file?.Flush(); }

    public static void Trace(string cat, string msg) => Write(LogLevel.Trace, cat, msg);
    public static void Debug(string cat, string msg) => Write(LogLevel.Debug, cat, msg);
    public static void Info(string cat, string msg) => Write(LogLevel.Info, cat, msg);
    public static void Warn(string cat, string msg) => Write(LogLevel.Warn, cat, msg);
    public static void Error(string cat, string msg) => Write(LogLevel.Error, cat, msg);

    /// <summary>Logs an exception with its type, message, and full stack at Error.</summary>
    public static void Exception(string cat, string context, Exception ex) =>
        Write(LogLevel.Error, cat, $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex}");

    /// <summary>True when the given level would be emitted — guard expensive formatting with this.</summary>
    public static bool Enabled(LogLevel level) => level >= Level;

    private static void Write(LogLevel level, string cat, string msg)
    {
        if (level < Level) return;
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{Tag(level)}] [{cat}] [t{Environment.CurrentManagedThreadId}] {msg}";
        lock (_gate)
        {
            if (level >= LogLevel.Warn) Console.Error.WriteLine(line);
            else Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };
}
