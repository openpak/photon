using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Per-room relay: holds the connected peers by actor number, runs the interceptor chain over an
/// inbound event, and fans the resulting event(s) out to every OTHER actor in the room. Driven from
/// the single-threaded UDP listener loop. The <c>_gate</c> lock guards ONLY the membership map and
/// the spawn cache (snapshot under the lock, send outside it); outbound sends themselves are NOT
/// serialized by <c>_gate</c> — cross-thread safety of a peer's sequence state relies on EnetPeer's
/// own internal locking.
/// </summary>
public sealed class RoomSession
{
    // PUN event codes carried on the relay (see PhotonCodes.PunEvent): Instantiation (202) is sent
    // with EventCaching.AddToRoomCache so the server replays it to late joiners; Destroy (204) removes
    // an instantiated object. We mirror Photon's room cache so a player who joins after others have
    // spawned still renders them.
    private readonly object _gate = new();
    private readonly Dictionary<int, PeerConnection> _members = new();
    private readonly InterceptorChain _chain;

    // Ordered spawn cache keyed by viewID (latest 202 per viewID wins; re-spawns replace prior).
    // Insertion order is preserved for deterministic replay; a viewID re-cached keeps its existing
    // slot's order but updates the event, so a later despawn/respawn doesn't reorder the world.
    // 202s with no resolvable viewID get a negative synthetic key so they still replay (never dropped).
    private readonly Dictionary<int, EventData> _spawnCache = new();
    private readonly List<int> _spawnOrder = new();
    private int _syntheticViewId = -1;     // monotonically decreasing keys for viewID-less spawns

    // Per-actor set of spawn viewIDs already delivered to that actor (via a live 202 relay or a cache
    // replay). The Join->Replay window means a newcomer can receive a live 202 for a viewID AND have
    // that viewID in the cache; ReplayCacheTo consults this set so each viewID reaches an actor exactly
    // once, regardless of how the live-202 / join / replay calls interleave. Cleared on Leave so a
    // reconnecting actor renders the world afresh.
    private readonly Dictionary<int, HashSet<int>> _deliveredSpawns = new();

    public string RoomName { get; }

    public RoomSession(string roomName, InterceptorChain chain)
    {
        RoomName = roomName; _chain = chain;
    }

    public void Join(int actor, PeerConnection peer) { lock (_gate) _members[actor] = peer; }
    public void Leave(int actor) { lock (_gate) { _members.Remove(actor); _deliveredSpawns.Remove(actor); } }
    public int Count { get { lock (_gate) return _members.Count; } }

    /// <summary>Snapshot of the actor numbers currently in the room (relay membership).</summary>
    public IReadOnlyList<int> Actors() { lock (_gate) return _members.Keys.ToArray(); }

    /// <summary>
    /// Server-originated send to every member (no sender exclusion, no interceptors). For admin/debug
    /// fan-out (e.g. a server announcement). Must run on the listener thread; returns the recipient count.
    /// </summary>
    public int SendToAll(EventData ev, bool unreliable = false)
    {
        List<PeerConnection> recipients;
        lock (_gate) recipients = new List<PeerConnection>(_members.Values);
        foreach (var peer in recipients) peer.RaiseEvent(ev, unreliable);
        return recipients.Count;
    }

    /// <summary>Server-originated send to one member; false if that actor isn't present. Listener thread only.</summary>
    public bool SendToActor(int actor, EventData ev, bool unreliable = false)
    {
        PeerConnection? peer;
        lock (_gate) { if (!_members.TryGetValue(actor, out peer)) return false; }
        peer.RaiseEvent(ev, unreliable);
        return true;
    }

    /// <summary>
    /// Removes an actor from the room (a "soft kick"): optionally notifies the kicked player with a
    /// ServerMessage, drops it from the relay, and tells the remaining actors it left (event 254).
    /// Listener thread only; false if the actor wasn't present.
    /// </summary>
    public bool Kick(int actor, string? reason = null)
    {
        lock (_gate) { if (!_members.ContainsKey(actor)) return false; }
        if (reason is not null) SendToActor(actor, ChatCommandHandler.ServerMessageEvent(reason));
        Leave(actor);
        SendToAll(new EventData(PhotonCodes.Event.Leave, new() { { PhotonCodes.Param.ActorNr, actor } }));
        return true;
    }

    /// <summary>Runs the interceptor chain over <paramref name="ev"/> and fans the verdict out to
    /// every actor except <paramref name="senderActor"/>.</summary>
    public void RelayFrom(int senderActor, EventData ev, bool unreliable = false)
    {
        var verdict = _chain.Run(new EventContext(RoomName, senderActor, ev, unreliable));
        if (verdict.Action == RelayAction.Drop) return;

        List<PeerConnection> recipients;
        lock (_gate)
        {
            // Maintain the room spawn cache under the same lock that guards membership.
            int spawnKey = 0;
            if (verdict.Event is { Code: PhotonCodes.PunEvent.Instantiation }) spawnKey = CacheSpawn(verdict.Event);
            else if (verdict.Event is { Code: PhotonCodes.PunEvent.Destroy }) EvictSpawn(verdict.Event);

            recipients = new List<PeerConnection>(_members.Count);
            // When relaying a live 202, record its cache key as delivered to each recipient so a later
            // ReplayCacheTo to that same actor won't re-send it (the Join->Replay double-spawn window).
            // CacheSpawn returns the exact key it used (the viewID, or a synthetic key) so dedupe keys
            // match what ReplayCacheTo iterates.
            bool isSpawn = verdict.Event is { Code: PhotonCodes.PunEvent.Instantiation };
            foreach (var (actor, peer) in _members)
            {
                if (actor == senderActor) continue;
                recipients.Add(peer);
                if (isSpawn) MarkDelivered(actor, spawnKey);
            }
        }

        foreach (var peer in recipients)
        {
            if (verdict.Event is not null) peer.RaiseEvent(verdict.Event, unreliable);
            foreach (var extra in verdict.Originated) peer.RaiseEvent(extra, unreliable);
        }
    }

    /// <summary>
    /// Replays the room's cached instantiation events to a single newly-joined actor (reliably, in
    /// insertion order) so the newcomer renders the world that already exists. Mirrors how the Photon
    /// server flushes EventCaching.AddToRoomCache to a late joiner. A snapshot is taken under the lock
    /// and the sends happen outside it, matching the <see cref="RelayFrom"/> pattern.
    /// </summary>
    public void ReplayCacheTo(int actor)
    {
        PeerConnection? peer;
        List<EventData> snapshot;
        lock (_gate)
        {
            if (!_members.TryGetValue(actor, out peer)) return;
            snapshot = new List<EventData>(_spawnOrder.Count);
            foreach (var key in _spawnOrder)
            {
                // Skip any spawn already delivered to this actor by a live 202 relay — otherwise the
                // Join->Replay window would double-instantiate it. Mark the rest as delivered as we send.
                if (IsDelivered(actor, key)) continue;
                if (_spawnCache.TryGetValue(key, out var ev)) { snapshot.Add(ev); MarkDelivered(actor, key); }
            }
        }

        foreach (var ev in snapshot) peer.RaiseEvent(ev);   // reliable: spawns must not be dropped
    }

    /// <summary>Records that spawn <paramref name="key"/> has been delivered to <paramref name="actor"/>. Must hold <c>_gate</c>.</summary>
    private void MarkDelivered(int actor, int key)
    {
        if (!_deliveredSpawns.TryGetValue(actor, out var set)) _deliveredSpawns[actor] = set = new HashSet<int>();
        set.Add(key);
    }

    /// <summary>True if spawn <paramref name="key"/> was already delivered to <paramref name="actor"/>. Must hold <c>_gate</c>.</summary>
    private bool IsDelivered(int actor, int key)
        => _deliveredSpawns.TryGetValue(actor, out var set) && set.Contains(key);

    /// <summary>Caches a 202 keyed by its viewID (latest wins, existing order preserved) and returns
    /// the key it used (the viewID, or a synthetic key for viewID-less spawns). Must hold <c>_gate</c>.</summary>
    private int CacheSpawn(EventData ev)
    {
        int key = TryReadViewId(ev, out var viewId) ? viewId : _syntheticViewId--;
        if (!_spawnCache.ContainsKey(key)) _spawnOrder.Add(key);
        _spawnCache[key] = ev;   // re-spawn / update of the same viewID replaces the prior entry
        return key;
    }

    /// <summary>Evicts the cached spawn a 204 destroy refers to, so a despawned object is not replayed.
    /// Must hold <c>_gate</c>.</summary>
    private void EvictSpawn(EventData ev)
    {
        if (!TryReadDestroyViewId(ev, out var viewId)) return;   // can't resolve: leave cached (re-instantiate is tolerated)
        if (_spawnCache.Remove(viewId)) _spawnOrder.Remove(viewId);
    }

    /// <summary>Reads the viewID from a 202's PData(245) hashtable at key 7 (PUN's instantiation viewID slot).</summary>
    private static bool TryReadViewId(EventData ev, out int viewId)
    {
        viewId = 0;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var raw) || raw is not IDictionary<object, object> pdata) return false;
        if (pdata.TryGetValue(PhotonCodes.InstantiationKey.ViewId, out var v) && v is int i) { viewId = i; return true; }
        return false;
    }

    /// <summary>
    /// Best-effort viewID extraction from a 204 destroy. PUN's destroy payload is less rigidly shaped
    /// than the 202 instantiation, so we first try the viewID slot (key 7), then fall back to scanning
    /// the PData hashtable values for any int that matches a currently-cached viewID. If nothing matches
    /// we return false and the caller conservatively keeps the spawn (replaying a stale spawn is far less
    /// harmful than dropping a live one — the client tolerates a re-instantiate).
    /// </summary>
    private bool TryReadDestroyViewId(EventData ev, out int viewId)
    {
        viewId = 0;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var raw) || raw is not IDictionary<object, object> pdata) return false;
        if (pdata.TryGetValue(PhotonCodes.InstantiationKey.ViewId, out var v) && v is int i && _spawnCache.ContainsKey(i)) { viewId = i; return true; }
        foreach (var value in pdata.Values)
            if (value is int candidate && _spawnCache.ContainsKey(candidate)) { viewId = candidate; return true; }
        return false;
    }
}
