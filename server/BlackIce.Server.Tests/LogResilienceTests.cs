using System;
using System.IO;
using BlackIce.Server.Core;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Serializes tests that mutate the process-global <see cref="Log"/> static (its <c>Level</c> or file sink)
/// so they don't race each other under xUnit's default cross-class parallelism. Any test class touching
/// <c>Log</c> should carry <c>[Collection("Log")]</c>.
/// </summary>
[CollectionDefinition("Log")]
public sealed class LogCollection { }

/// <summary>
/// The logger must never let a file-sink I/O failure (classically a full disk) propagate — Log.Write runs
/// on the UDP listener thread per-packet under --trace, so a throw there would stall gameplay (that's the
/// bug that froze the bots when the disk filled). On a write failure it drops the file sink and continues
/// console-only.
///
/// NOTE: Log is a process-global static; these tests mutate it, so they share a collection to avoid running
/// concurrently with anything else that touches Log.
/// </summary>
[Collection("Log")]
public class LogResilienceTests
{
    [Fact]
    public void Write_and_Flush_are_exception_free_on_the_hot_path()
    {
        // The contract the listener thread depends on: per-packet Write + Flush never throw. Drive the
        // Trace hot path against a healthy temp sink and assert no exception escapes.
        var prev = Log.Level;
        var path = Path.Combine(Path.GetTempPath(), $"blackice-log-{Guid.NewGuid():N}.log");
        try
        {
            Log.Level = LogLevel.Trace;
            Log.ToFile(path);
            var ex = Record.Exception(() =>
            {
                for (int i = 0; i < 50; i++) Log.Trace("test", $"hot-path line {i}");
                Log.Flush();
            });
            Assert.Null(ex);
        }
        finally
        {
            Log.Level = prev;
            // Re-point to a fresh sink so the handle on `path` is released, then delete.
            var safe = Path.Combine(Path.GetTempPath(), $"blackice-log-{Guid.NewGuid():N}.log");
            Log.ToFile(safe);
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(safe); } catch (IOException) { }
        }
    }

    [Fact]
    public void ToFile_on_an_unwritable_path_does_not_crash_the_caller()
    {
        // Opening the sink on an invalid path must not throw into startup — a bad/full log location should
        // degrade to console-only, not take the server down. (Uses a path under a file, which can't be a dir.)
        var prev = Log.Level;
        try
        {
            var blocker = Path.Combine(Path.GetTempPath(), $"blackice-blocker-{Guid.NewGuid():N}");
            File.WriteAllText(blocker, "x");                 // a FILE …
            var bad = Path.Combine(blocker, "cannot", "open.log");   // … so this sub-path is unopenable
            var ex = Record.Exception(() => Log.ToFile(bad));
            Assert.Null(ex);                                  // ToFile swallows the open failure
            // And subsequent logging still works (console-only) without throwing.
            Assert.Null(Record.Exception(() => Log.Info("test", "after a failed ToFile")));
            File.Delete(blocker);
        }
        finally
        {
            Log.Level = prev;
            var safe = Path.Combine(Path.GetTempPath(), $"blackice-log-{Guid.NewGuid():N}.log");
            Log.ToFile(safe);   // restore a healthy sink for any later log calls
            try { File.Delete(safe); } catch (IOException) { }
        }
    }
}
