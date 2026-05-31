namespace BlackIce.Server.Common;

/// <summary>
/// Where the Result handling policies emit their diagnostics. Common sits below the logging layer, so
/// the host wires these to the real <c>Log</c> at startup; until then they fall back to stderr. This
/// keeps the Result primitives dependency-free while still routing into the server's log.
/// </summary>
public static class ResultDiagnostics
{
    public static Action<string, string> Warn { get; set; } =
        (category, message) => Console.Error.WriteLine($"[WRN] [{category}] {message}");

    public static Action<string, string> Error { get; set; } =
        (category, message) => Console.Error.WriteLine($"[ERR] [{category}] {message}");
}

/// <summary>Thrown by <see cref="ResultPolicies.Expect(Result,string)"/> — the C# analogue of DuetOS's RESULT_EXPECT panic.</summary>
public sealed class ResultExpectException : Exception
{
    public ErrorCode Code { get; }
    public SourceLocation Origin { get; }

    public ResultExpectException(Err error, string message)
        : base($"{message}: {error}")
    {
        Code = error.Code;
        Origin = error.Location;
    }
}

/// <summary>
/// The three explicit ways to consume a <see cref="Result"/>, mirroring DuetOS's RESULT_TRY /
/// RESULT_LOG_AND_DROP / RESULT_EXPECT so each policy is greppable:
///
/// <list type="bullet">
/// <item><b>Propagate</b> — the caller is also Result-returning: <c>if (r.IsFail) return r.AsErr();</c>
/// or chain with <see cref="Result{T}.Bind{TU}"/> / <see cref="Result{T}.Map{TU}"/>.</item>
/// <item><b>Log and continue</b> — best-effort work that must not abort the surrounding flow:
/// <see cref="LogAndDrop(Result,string,string)"/>.</item>
/// <item><b>Expect</b> — failure indicates a programmer bug, not a runtime condition:
/// <see cref="Expect(Result,string)"/> throws.</item>
/// </list>
///
/// A bare ignore (<c>_ = expr;</c>) on a Result is discouraged: it compiles but tells the next reader nothing.
/// </summary>
public static class ResultPolicies
{
    /// <summary>LOG AND CONTINUE: on failure, emit one warn line (with code + origin); never throws.</summary>
    public static void LogAndDrop(this Result result, string category, string label)
    {
        if (result.IsOk) return;
        ResultDiagnostics.Warn(category, $"{label}: {result.AsErr()}");
    }

    /// <summary>LOG AND CONTINUE for a value result: returns the value on success, or <paramref name="fallback"/> after logging.</summary>
    public static T LogAndDrop<T>(this Result<T> result, string category, string label, T fallback = default!)
    {
        if (result.IsOk) return result.Value;
        ResultDiagnostics.Warn(category, $"{label}: {result.AsErr()}");
        return fallback;
    }

    /// <summary>EXPECT: on failure, log an error and throw <see cref="ResultExpectException"/>. Use only for invariants.</summary>
    public static void Expect(this Result result, string message)
    {
        if (result.IsOk) return;
        ResultDiagnostics.Error("Expect", $"{message}: {result.AsErr()}");
        throw new ResultExpectException(result.AsErr(), message);
    }

    /// <summary>EXPECT for a value result: returns the value, or logs and throws on failure.</summary>
    public static T Expect<T>(this Result<T> result, string message)
    {
        if (result.IsOk) return result.Value;
        ResultDiagnostics.Error("Expect", $"{message}: {result.AsErr()}");
        throw new ResultExpectException(result.AsErr(), message);
    }
}
