namespace BlackIce.Server.Common;

/// <summary>
/// Runs a cleanup action on scope exit unless <see cref="Dismiss"/> is called first — the C# analogue
/// of DuetOS's DUETOS_DEFER / ScopeGuard (and Go's <c>defer</c>). Use it to release a resource acquired
/// partway through a fallible sequence, then dismiss on the success path so ownership hands off:
///
/// <code>
/// using var guard = Defer.Run(() => thing.Release());
/// // ... fallible steps that may early-return ...
/// guard.Dismiss();   // success: keep the thing
/// return thing;
/// </code>
///
/// C# already has <c>using</c>/<c>try-finally</c> for unconditional cleanup; this adds the dismissable
/// "release only if a later step fails" shape they don't express cleanly.
/// </summary>
public sealed class ScopeGuard : IDisposable
{
    private Action? _cleanup;

    public ScopeGuard(Action cleanup) => _cleanup = cleanup;

    /// <summary>Retire the guard so the deferred action will NOT run on scope exit (the success/hand-off path).</summary>
    public void Dismiss() => _cleanup = null;

    public void Dispose()
    {
        var action = _cleanup;
        _cleanup = null;       // idempotent: dispose runs the action at most once
        action?.Invoke();
    }
}

/// <summary>Factory for <see cref="ScopeGuard"/>: <c>using var g = Defer.Run(() => cleanup());</c>.</summary>
public static class Defer
{
    public static ScopeGuard Run(Action cleanup) => new(cleanup);
}
