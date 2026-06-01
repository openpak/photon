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

    /// <summary>Opens (or replaces) the file sink. Safe to call once at startup. A failure to open the file
    /// (bad path, full/read-only disk) degrades to console-only rather than throwing — a logging problem must
    /// never take the server down.</summary>
    public static void ToFile(string path)
    {
        lock (_gate)
        {
            try { _file?.Flush(); _file?.Dispose(); } catch (IOException) { /* discarding a broken sink */ }
            _file = null;
            try
            {
                _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,   // crash-safety: we want every line on disk even if the process dies
                };
                _file.WriteLine($"# BlackIce.Server log — {DateTime.UtcNow:O}");
                FileSinkDisabled = false;   // a fresh, healthy sink re-enables file logging
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _file = null;
                FileSinkDisabled = true;
                try { Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [WRN] [Log] could not open log file '{path}' ({ex.GetType().Name}); continuing console-only"); }
                catch (IOException) { /* console gone too — nothing we can do */ }
            }
        }
    }

    public static void Flush() { lock (_gate) { try { _file?.Flush(); } catch (IOException) { DisableFileSink(); } } }

    /// <summary>True once a file-write failure (e.g. disk full) has disabled the file sink for this run.</summary>
    public static bool FileSinkDisabled { get; private set; }

    /// <summary>Drops the file sink after an I/O failure so logging never again touches the bad file. Must
    /// hold <c>_gate</c>. Console logging continues; a disk problem must not be able to stall the server.</summary>
    private static void DisableFileSink()
    {
        if (FileSinkDisabled) return;
        FileSinkDisabled = true;
        try { _file?.Dispose(); } catch (IOException) { /* already broken — nothing more to do */ }
        _file = null;
        // Console-only from here; surfaces once so an operator knows the log file stopped (e.g. disk full).
        try { Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [WRN] [Log] file sink disabled after an I/O error (disk full?); continuing console-only"); }
        catch (IOException) { /* console gone too — give up silently rather than throw into a caller */ }
    }

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
            // A file-write failure (classically a full disk) must NOT propagate — Write runs on the UDP
            // listener thread (per-packet under --trace), so an exception here would stall gameplay. Drop the
            // file sink and carry on console-only; that's exactly the bug that froze the bots on a full disk.
            if (_file is { } f)
            {
                try { f.WriteLine(line); }
                catch (IOException) { DisableFileSink(); }
            }
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
