using System.Collections.Concurrent;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// A cross-thread queue of admin/debug actions that must run on the Game listener thread — anything
/// that sends packets touches per-peer transport state, which only that single thread may do. The
/// console (or a future remote-admin endpoint) enqueues; the listener drains it each maintenance tick,
/// the same discipline the playerbot spawner uses. A throwing action is logged, never fatal.
/// </summary>
public sealed class AdminActionQueue
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Enqueue(Action action) => _queue.Enqueue(action);

    /// <summary>Runs all queued actions on the calling (listener) thread.</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Log.Exception("Admin", "queued admin action failed", ex); }
        }
    }
}
