using System.Collections.Concurrent;
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Authority;
using BlackIce.Server.LoadBalancing.Navigation;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// Owns the live bots: reserves non-colliding actor numbers, spawns them into a room session, and
/// drives their per-tick movement. Bot actors start at <see cref="BotActorBase"/> so their
/// viewID blocks (actor*1000) never overlap real players' blocks. Tick is driven off the host's
/// 1 Hz maintenance loop (the single-threaded Game listener thread).
///
/// All mutation of <c>_bots</c> and all spawn-relay (which touches every real peer's EnetPeer
/// sequence state) MUST happen on that listener thread. A console-thread caller therefore uses
/// <see cref="RequestSpawn"/>, which only enqueues; <see cref="Tick"/> drains the queue and performs
/// the actual spawn on the listener thread. The synchronous <see cref="Spawn"/> remains for callers
/// that are already on a single thread (tests).
/// </summary>
public sealed class BotManager
{
    public const int BotActorBase = 10000;

    /// <summary>When true, each tick every bot also relays the next entry of its <see cref="GameActions"/>
    /// script — a rotating mix of legitimate and cheating gameplay traffic — to exercise the relay and
    /// authority/anti-cheat surface. Off by default (bots just move).</summary>
    public bool EmitGameActions { get; set; }

    /// <summary>Optional game-mode registry; when set, bots spawned into a team-mode room are assigned a
    /// balanced team (so the soak exercises Team-vs-Team / Co-op friendly-fire enforcement too).</summary>
    public GameModeRegistry? Modes { get; set; }

    /// <summary>When true (and <see cref="Worlds"/> is set), auto-spawned bots use the world-aware
    /// <see cref="HunterBehavior"/> — they seek and act on real enemies/hack-nodes/loot — instead of the
    /// blind <see cref="WanderBehavior"/>. Off by default.</summary>
    public bool Smart { get; set; }

    /// <summary>Shared per-room world-state the smart bots read to find targets. Set from the host so bots
    /// and the authority plugin observe the same world. Null = bots fall back to move-only behavior.</summary>
    public RoomWorldStateRegistry? Worlds { get; set; }

    /// <summary>Navmesh cache (by map name) the smart bots path on. Set from the host. Null, or no map
    /// associated with a room, → bots fall back to today's player-anchor movement (no navmesh).</summary>
    public NavMeshRegistry? Navs { get; set; }

    /// <summary>Room name → navmesh map name (e.g. "Realm13" → "level13"), from each realm's
    /// <c>ExtraJson</c> "navmesh" key. Rooms absent here have no navmesh and use the fallback movement.</summary>
    public IReadOnlyDictionary<string, string>? RoomMaps { get; set; }

    private int _nextBotActor = BotActorBase;
    private int _spawnedCount;   // monotonic, for deterministic spread placement of smart bots
    private readonly List<(PlayerBot bot, RoomSession session, IBotBehavior behavior)> _bots = new();
    private readonly Dictionary<int, int> _scriptCursor = new();   // bot actor -> next action index
    private readonly ConcurrentQueue<(RoomSession session, BotIdentity identity, IBotBehavior? behavior)> _pending = new();

    // Live bot count per room. Written on the Game listener thread (spawn) and read from the Master
    // thread (lobby browser), so it is a concurrent map for cross-thread safety.
    private readonly ConcurrentDictionary<string, int> _countByRoom = new();

    /// <summary>The number of bots currently in <paramref name="room"/> (0 if none). Thread-safe.</summary>
    public int CountIn(string room) => _countByRoom.TryGetValue(room, out var n) ? n : 0;

    /// <summary>
    /// Spawns a bot synchronously on the CALLING thread. Mutates <c>_bots</c> and relays the bot's
    /// join/instantiate to every real peer, so the caller must be the listener thread (or a test on
    /// its own single thread). Cross-thread callers (the console) must use <see cref="RequestSpawn"/>.
    /// </summary>
    public PlayerBot Spawn(RoomSession session, BotIdentity identity, IBotBehavior? behavior = null)
    {
        int index = _spawnedCount++;
        // Spawn AT a live player's position (offset into a small ring) — the only ground the server can
        // prove is safe, since a player is standing on it. The server has no level geometry, so a guessed
        // world coordinate (e.g. origin) can drop a bot into a map hazard ("Lava"/void). With no player
        // anchor known, fall back to origin (tests / pre-join); the Tick gate holds smart spawns until an
        // anchor exists so production never uses that fallback.
        var anchor = PlayerAnchor(session.RoomName) ?? (0f, 0f, 0f);
        var (ox, oz) = RingOffset(index);
        float sx = anchor.x + ox, sy = anchor.y, sz = anchor.z + oz;
        var bot = new PlayerBot(_nextBotActor++, identity);
        bot.Spawn(session, sx, sy, sz);   // 202 carries this position so the client renders the bot on safe ground
        _bots.Add((bot, session, behavior ?? DefaultBehavior(bot, index, sx, sy, sz, session.RoomName)));
        _countByRoom.AddOrUpdate(session.RoomName, 1, (_, n) => n + 1);
        // In a team-mode room, give the bot a team so it participates in friendly-fire/PvE enforcement.
        if (Modes is not null && Modes.ModeOf(session.RoomName) != GameMode.FreeForAll)
        {
            int team = Modes.AssignTeam(session.RoomName, bot.Actor);
            Log.Info("Bots", $"bot {bot.Actor} joined \"{session.RoomName}\" on team {team}");
        }
        return bot;
    }

    /// <summary>
    /// Queues a bot spawn to run on the next <see cref="Tick"/> (i.e. on the listener thread). Safe
    /// to call from any thread (e.g. the console). Does not touch <c>_bots</c>, the relay, or the
    /// actor counter on the calling thread.
    /// </summary>
    public void RequestSpawn(RoomSession session, BotIdentity identity, IBotBehavior? behavior = null)
        => _pending.Enqueue((session, identity, behavior));

    /// <summary>Drains queued spawns then advances every bot one step and relays its new position
    /// (event 201, unreliable). Runs entirely on the listener thread.</summary>
    public void Tick()
    {
        // Drain queued spawns. A SMART bot needs a safe anchor (a live player's known position) before it
        // spawns — otherwise it materializes at a guessed coordinate that may be a map hazard. Hold (requeue)
        // any spawn whose room has no player anchor yet; it spawns the moment a player is present. The
        // pendingCount bound stops a requeue from looping within one tick. Non-smart bots spawn immediately.
        int pendingCount = _pending.Count;
        for (int i = 0; i < pendingCount && _pending.TryDequeue(out var req); i++)
        {
            if (Smart && Worlds is not null && PlayerAnchor(req.session.RoomName) is null)
            {
                _pending.Enqueue(req);   // no safe anchor yet — wait for a player to join
                continue;
            }
            Spawn(req.session, req.identity, req.behavior);   // deferred spawn on this (listener) thread
        }

        foreach (var (bot, session, behavior) in _bots)
        {
            // World-aware brain: read the room's shared world-state, move toward a real target, and relay
            // any contextual action RPCs it produced. Falls back to move-only when no world-state is wired.
            if (behavior is IBotBrain brain && Worlds is { } worlds)
            {
                var step = brain.Think(worlds.For(session.RoomName));
                session.RelayFrom(bot.Actor, BuildPositionEvent(bot.ViewId, step.Position), unreliable: true);
                foreach (var ev in step.Actions)
                    session.RelayFrom(bot.Actor, ev, unreliable: ev.Code == PhotonCodes.PunEvent.SendSerialize);
                if (step.Actions.Count > 0) Log.Info("Bots", $"bot {bot.Actor} -> {step.Label}");
                continue;
            }

            var p = behavior.Tick();
            session.RelayFrom(bot.Actor, BuildPositionEvent(bot.ViewId, p), unreliable: true);
            if (EmitGameActions) EmitNextAction(bot, session);
        }
    }

    /// <summary>
    /// Teleports every bot in <paramref name="room"/> to that room's player position (a small ring around it),
    /// relaying the move so clients see them arrive — the admin <c>summon</c> command's worker. Brings the
    /// fleet to the human so they're reachable. Returns the number summoned, or -1 if no player position is
    /// known yet. MUST run on the Game listener thread (it relays per-peer): the console queues it via
    /// <see cref="AdminActionQueue"/>.
    /// </summary>
    public int SummonAll(string room)
    {
        if (PlayerAnchor(room) is not { } anchor) return -1;
        int n = 0;
        int i = 0;
        foreach (var (bot, session, behavior) in _bots)
        {
            if (!string.Equals(session.RoomName, room, System.StringComparison.Ordinal)) continue;
            var (ox, oz) = RingOffset(i++);
            float x = anchor.x + ox, y = anchor.y, z = anchor.z + oz;
            behavior.Teleport(x, y, z);
            session.RelayFrom(bot.Actor, BuildPositionEvent(bot.ViewId, new BotPositionUpdate(x, y, z)), unreliable: true);
            n++;
        }
        if (n > 0) Log.Info("Bots", $"summoned {n} bot(s) to the player in \"{room}\"");
        return n;
    }

    /// <summary>
    /// Picks the default behavior for an auto-spawned bot: the world-aware <see cref="HunterBehavior"/> when
    /// <see cref="Smart"/> + <see cref="Worlds"/> are set, otherwise the blind <see cref="WanderBehavior"/>.
    /// Smart bots start spread on a deterministic spiral (so they aren't stacked before they find targets) —
    /// they then path to real entities the master spawns. The spiral uses the golden angle for even coverage.
    /// When the room's realm associated a navmesh (and the artifact is present), the hunter is handed it so it
    /// paths on the real walkable surface; otherwise it's null and the hunter keeps today's behavior.
    /// </summary>
    private IBotBehavior DefaultBehavior(PlayerBot bot, int index, float sx, float sy, float sz, string room)
    {
        if (!Smart || Worlds is null) return new WanderBehavior(sx, sz);
        return new HunterBehavior(bot.ViewId, sx, sz, startY: sy, fleetIndex: index, seed: bot.Actor,
                                  navMesh: NavMeshFor(room));
    }

    /// <summary>The navmesh for <paramref name="room"/> — its realm's mapped map name resolved through the
    /// <see cref="Navs"/> cache — or null when no registry is wired, no map is associated, or the artifact is
    /// absent (the graceful fallback to player-anchor movement).</summary>
    private NavMesh? NavMeshFor(string room)
    {
        if (Navs is null || RoomMaps is null) return null;
        return RoomMaps.TryGetValue(room, out var mapName) ? Navs.For(mapName) : null;
    }

    /// <summary>A small golden-angle ring offset for bot <paramref name="index"/>, added to the spawn anchor
    /// so bots appear spread in a tight cluster around the player rather than stacked on one point.</summary>
    private static (float x, float z) RingOffset(int index)
    {
        const float goldenAngle = 2.399963f;   // radians (~137.5°)
        float radius = 3f + (index % 5) * 1.5f;
        float angle = index * goldenAngle;
        return (radius * (float)System.Math.Cos(angle), radius * (float)System.Math.Sin(angle));
    }

    /// <summary>
    /// The position of a live, non-bot player in <paramref name="room"/> whose location the world-state
    /// knows (from their 201 stream), or null if none. This is the only position the server can prove is
    /// safe ground — a player is standing on it — so bots anchor their spawn here instead of guessing a
    /// world coordinate that may be a hazard. Bot avatars (also "Player" kind) are excluded by viewID range.
    /// </summary>
    private (float x, float y, float z)? PlayerAnchor(string room)
    {
        if (Worlds?.Find(room) is not { } world) return null;
        var p = world.Nearest(
            e => e.ViewId < BotActorBase * 1000 && string.Equals(e.Kind, "Player", System.StringComparison.OrdinalIgnoreCase),
            0f, 0f);
        return p is null ? null : (p.X, p.Y, p.Z);
    }

    /// <summary>Relays the bot's next scripted game action through the room (so the interceptor chain
    /// sees it), advancing its cursor. Cheats are logged with a CHEAT marker so the soak output is legible.</summary>
    private void EmitNextAction(PlayerBot bot, RoomSession session)
    {
        var script = GameActions.Script(bot);
        if (script.Count == 0) return;
        _scriptCursor.TryGetValue(bot.Actor, out var i);
        _scriptCursor[bot.Actor] = i + 1;
        var action = script[i % script.Count];

        foreach (var ev in action.Events)
            session.RelayFrom(bot.Actor, ev, unreliable: ev.Code == PhotonCodes.PunEvent.SendSerialize);
        Log.Info("Bots", $"bot {bot.Actor} -> {action.Label}{(action.Events.Count > 1 ? $" x{action.Events.Count}" : "")}");
    }

    /// <summary>
    /// Builds the event-201 serialize batch moving the bot's avatar. The client's
    /// NetworkSyncPosition.OnPhotonSerializeView reads a FIXED field set with no bounds check, so the
    /// per-view stream must carry every field the Player prefab's enabled body component emits or the
    /// receiver throws IndexOutOfRangeException. After pos(Vec3) and rot(Quat) the confirmed body fields
    /// (from a captured real event-201) are the SyncPlayerHP triplet + SyncHead pitch:
    /// damageTaken, maxHealth, tempHP, headPitch. We carry only that single body component (no buff/weapon
    /// tail) so we never invent fields we cannot verify.
    /// </summary>
    private static EventData BuildPositionEvent(int viewId, BotPositionUpdate p)
    {
        var pos = PunVector3(p.X, p.Y, p.Z);
        var rot = PunQuaternion(0, 0, 0, 1);
        var view = new object[] { viewId, false, null!, pos, rot, 0f, 200f, 0f, 0f };
        var batch = new object[] { System.Environment.TickCount, null!, view };
        return new EventData(PhotonCodes.PunEvent.SendSerialize, new() { { PhotonCodes.Param.Data, batch } });
    }

    private static PhotonCustomData PunVector3(float x, float y, float z)
    {
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }

    private static PhotonCustomData PunQuaternion(float x, float y, float z, float w)
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(12), w);
        return new PhotonCustomData(PhotonCodes.CustomType.Quaternion, b);
    }
}
