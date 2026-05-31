# Phase 2c — Playerbot scaffolding

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development / executing-plans. Steps use `- [ ]`.

**Goal:** A server-originated synthetic player (no client, no real user) that real clients render as a normal player: it joins, sets a generated identity/appearance, instantiates its avatar, and wanders. Built entirely server-side on the existing relay (mod-free, per recon).

**Architecture:** A `PlayerBot` owns a reserved actor number + viewID and emits the exact post-join event sequence a real player emits, through `RoomSession.RelayFrom(botActor, ev)` (which fans out to all real members since the bot is not itself a member). A `BotBehavior` produces per-tick position events; a `BotManager` spawns bots and drives ticks off the listener's 1 Hz maintenance cadence (single-threaded → no locking). Identity is generated from pools (recon: all appearance props are free-form, unvalidated, no Steam binding).

**Tech Stack:** C#/.NET 8, xUnit. Builds on `RoomSession`/`RoomRegistry`/`EventData` and the codec.

**Recon basis (already done — see spec Background + memory/game-join-requires-opsetproperties):** a real player after join emits, in order: join event 255 → OpSetProperties identity (PlayerLevel/Cheater/Ping + PlayerModelIndex/ModelColors/BackHolo…) → instantiate avatar event 202 (prefab "Player", viewID = actor*1000+1, cached) → RefreshModel RPC (event 200) → position stream (event 201). ViewID namespace is owned by the server (actor*1000+subId). No client-side validation/Steam check blocks a fabricated actor.

**Scope (YAGNI):** lifecycle + identity generation + a simple wander behavior (position stream) + manual spawn entry point. NOT in scope: combat AI, pathfinding, bot persistence, population policy, late-joiner cache replay. Those are later.

**Self-verifiability (why this is the autonomous-friendly subsystem):** every step is unit-testable by capturing events into a fake `PeerConnection` (via `OnRaised`), exactly like the relay tests. A live check (bot visibly wanders in a real client) is a bonus gate for the user, not required to land the code.

---

## Task 1: BotIdentity + generator

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/Bots/BotIdentity.cs`
- Create: `server/BlackIce.Server.LoadBalancing/Bots/BotIdentityGenerator.cs`
- Test: `server/BlackIce.Server.Tests/Bots/BotIdentityGeneratorTests.cs`

- [ ] **Step 1 — failing test:**

```csharp
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class BotIdentityGeneratorTests
{
    [Fact]
    public void Generates_a_nonempty_name_and_in_range_model()
    {
        var id = new BotIdentityGenerator(seed: 42).Next();
        Assert.False(string.IsNullOrWhiteSpace(id.Name));
        Assert.InRange(id.ModelIndex, 0, 31);
        Assert.Equal(4, id.ModelColors.Length);   // main/secondary/tertiary/quaternary RGBA
    }

    [Fact]
    public void Successive_identities_differ()
    {
        var g = new BotIdentityGenerator(seed: 7);
        var a = g.Next(); var b = g.Next();
        Assert.NotEqual(a.Name, b.Name);
    }
}
```

- [ ] **Step 2 — run, expect FAIL:** `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter BotIdentityGeneratorTests`

- [ ] **Step 3 — implement.** Create `server/BlackIce.Server.LoadBalancing/Bots/BotIdentity.cs`:

```csharp
namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>A generated synthetic-player identity. All fields are free-form (the client does not
/// validate them) and map to the player custom properties a real client sets after joining.</summary>
public sealed record BotIdentity(string Name, int ModelIndex, float[][] ModelColors, int Level, int Team);
```

Create `server/BlackIce.Server.LoadBalancing/Bots/BotIdentityGenerator.cs`:

```csharp
namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>Produces varied, free-form bot identities from fixed pools + a seeded RNG (deterministic for tests).</summary>
public sealed class BotIdentityGenerator
{
    private static readonly string[] Adjectives = { "Rogue", "Silent", "Iron", "Neon", "Ghost", "Razor", "Vex", "Null" };
    private static readonly string[] Nouns = { "Runner", "Spike", "Cipher", "Wraith", "Byte", "Hex", "Daemon", "Probe" };
    private readonly Random _rng;
    private int _counter;

    public BotIdentityGenerator(int? seed = null) => _rng = seed is int s ? new Random(s) : new Random();

    public BotIdentity Next()
    {
        var name = $"{Adjectives[_rng.Next(Adjectives.Length)]}{Nouns[_rng.Next(Nouns.Length)]}{++_counter}";
        var colors = new float[4][];
        for (int i = 0; i < 4; i++)
            colors[i] = new[] { (float)_rng.NextDouble(), (float)_rng.NextDouble(), (float)_rng.NextDouble(), 1f };
        return new BotIdentity(name, _rng.Next(0, 32), colors, Level: 0, Team: 1);
    }
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/Bots/BotIdentity.cs server/BlackIce.Server.LoadBalancing/Bots/BotIdentityGenerator.cs server/BlackIce.Server.Tests/Bots/BotIdentityGeneratorTests.cs
git commit -m "feat(bots): BotIdentity + seeded free-form identity generator"
```

---

## Task 2: PlayerBot — emits the join+identity+instantiate lifecycle

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/Bots/PlayerBot.cs`
- Test: `server/BlackIce.Server.Tests/Bots/PlayerBotLifecycleTests.cs`

**Context:** `RoomSession.RelayFrom(int senderActor, EventData ev, bool unreliable=false)` runs the interceptor chain then sends to every member except `senderActor`. A bot's actor number is NOT a member, so `RelayFrom(botActor, ev)` reaches all real players — exactly the origination channel we want. Event/param codes (from GameServerHandler constants / recon): EvJoin=255, EvPunInstantiation=202, EvPunRpc=200, EvPropertiesChanged=253; param keys ActorNr=254, Properties=251, TargetActorNr=253, PData=245. ViewID = botActor*1000+1.

- [ ] **Step 1 — failing test** (`server/BlackIce.Server.Tests/Bots/PlayerBotLifecycleTests.cs`):

```csharp
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class PlayerBotLifecycleTests
{
    private static PeerConnection RealPeer(out List<EventData> raised)
    {
        var captured = new List<EventData>(); raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaised = captured.Add; return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static RoomSession Session() =>
        new("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));

    [Fact]
    public void Spawn_emits_join_then_identity_then_instantiate_in_order_to_real_players()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);

        var bot = new PlayerBot(actor: 2, new BotIdentityGenerator(seed: 1).Next());
        bot.Spawn(session);

        // The human sees, in order: join(255), a properties-changed(253), an instantiate(202), a refresh RPC(200).
        var codes = raised.ConvertAll(e => e.Code);
        Assert.Equal(255, codes[0]);
        Assert.Contains((byte)253, codes);
        Assert.Contains((byte)202, codes);
        Assert.Contains((byte)200, codes);
        // The instantiate carries the bot's viewID = actor*1000+1 = 2001 under PData(245)'s payload, key 7.
        var inst = raised.Find(e => e.Code == 202);
        Assert.NotNull(inst);
    }

    [Fact]
    public void Spawn_join_event_carries_the_bot_actor_number()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        new PlayerBot(actor: 5, new BotIdentityGenerator(seed: 1).Next()).Spawn(session);

        var join = raised.Find(e => e.Code == 255);
        Assert.NotNull(join);
        Assert.True(join!.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 5);
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement** `server/BlackIce.Server.LoadBalancing/Bots/PlayerBot.cs`:

```csharp
using System.Collections.Generic;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// A server-originated synthetic player. Owns a reserved actor number and viewID and emits the same
/// event sequence a real client sends after joining, fanned out to real players via the room session
/// (the bot is not a session member, so RelayFrom reaches every real actor). Mod-free: the unmodified
/// client renders it as a normal player. See docs/superpowers/specs Phase 2 Background for the recon.
/// </summary>
public sealed class PlayerBot
{
    private const byte EvJoin = 255, EvInstantiate = 202, EvRpc = 200, EvPropertiesChanged = 253;
    private const byte PActorNr = 254, PActorList = 252, PProperties = 251, PTargetActorNr = 253, PData = 245;

    public int Actor { get; }
    public int ViewId { get; }
    public BotIdentity Identity { get; }

    public PlayerBot(int actor, BotIdentity identity)
    {
        Actor = actor; Identity = identity; ViewId = actor * 1000 + 1;
    }

    /// <summary>Emits join → identity properties → avatar instantiate → model-refresh, to the room's real players.</summary>
    public void Spawn(RoomSession session)
    {
        // 1) Join (255): announce the new actor.
        session.RelayFrom(Actor, new EventData(EvJoin, new() { { PActorNr, Actor } }));

        // 2) Identity as a properties-changed (253): the client reads appearance from these.
        var props = new Dictionary<object, object>
        {
            { "PlayerLevel", Identity.Level },
            { "Cheater", false },
            { "ViralAchievement", false },
            { "PlayerModelIndex", Identity.ModelIndex },
            { "BackHoloIconIndex", 0 },
            { "BackHoloModdedKey", "" },
            { "Team", Identity.Team },
            // Model colors are PUN Color custom types (code 67); encoded as raw RGBA float bytes.
            { "ModelMainColor", Color(Identity.ModelColors[0]) },
            { "ModelSecondaryColor", Color(Identity.ModelColors[1]) },
            { "ModelTertiaryColor", Color(Identity.ModelColors[2]) },
            { "ModelQuaternaryColor", Color(Identity.ModelColors[3]) },
        };
        session.RelayFrom(Actor, new EventData(EvPropertiesChanged, new()
        {
            { PProperties, props },
            { PTargetActorNr, Actor },
        }));

        // 3) Instantiate the avatar (202): prefab "Player", our viewID, cached so late joiners see it.
        session.RelayFrom(Actor, new EventData(EvInstantiate, new()
        {
            { PData, new Dictionary<object, object>
                {
                    { (byte)0, "Player" },
                    { (byte)7, ViewId },
                } },
        }));

        // 4) RefreshModel RPC (200): nudge clients to pull appearance from the props set in step 2.
        session.RelayFrom(Actor, new EventData(EvRpc, new()
        {
            { (byte)244, (byte)200 },
            { PData, new Dictionary<object, object>
                {
                    { (byte)0, ViewId },
                    { (byte)3, "RefreshModel" },
                    { (byte)4, System.Array.Empty<object>() },
                } },
        }));
    }

    /// <summary>PUN Color custom type (code 67): four big-endian floats (r,g,b,a).</summary>
    private static PhotonCustomData Color(float[] rgba)
    {
        var b = new byte[16];
        for (int i = 0; i < 4; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i * 4), rgba[i]);
        return new PhotonCustomData(67, b);
    }
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/Bots/PlayerBot.cs server/BlackIce.Server.Tests/Bots/PlayerBotLifecycleTests.cs
git commit -m "feat(bots): PlayerBot emits join+identity+instantiate+refresh lifecycle to real players"
```

---

## Task 3: BotBehavior — per-tick wander producing a position event

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/Bots/IBotBehavior.cs`
- Create: `server/BlackIce.Server.LoadBalancing/Bots/WanderBehavior.cs`
- Test: `server/BlackIce.Server.Tests/Bots/WanderBehaviorTests.cs`

**Context:** The position stream is event 201 (unreliable). For 2a the relay forwards 201 payloads opaquely; for a *bot* we must construct a minimal valid 201 payload. The observed 201 payload shape (from trace) is an object[] like `[networkTime(int), null, [viewID, false, null, Vector3(86), Quaternion(81), ...]]`. A minimal mover only needs to move the avatar's transform. To avoid guessing the full serialize sub-stream, WanderBehavior produces the position as a structured `BotPositionUpdate` (viewId + x/y/z) and PlayerBot/ BotManager builds the 201 event from it; the EXACT 201 sub-stream layout is verified by the interop step in Task 5 before trusting it live. For now the behavior is pure and testable.

- [ ] **Step 1 — failing test:**

```csharp
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class WanderBehaviorTests
{
    [Fact]
    public void Produces_a_changing_position_each_tick()
    {
        var w = new WanderBehavior(startX: 0, startZ: 0, seed: 3);
        var p0 = w.Tick();
        var p1 = w.Tick();
        Assert.NotEqual((p0.X, p0.Z), (p1.X, p1.Z));   // it moves
    }

    [Fact]
    public void Stays_within_a_bounded_radius_of_start()
    {
        var w = new WanderBehavior(startX: 100, startZ: 100, seed: 9, radius: 5);
        for (int i = 0; i < 200; i++)
        {
            var p = w.Tick();
            Assert.InRange(p.X, 95f, 105f);
            Assert.InRange(p.Z, 95f, 105f);
        }
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement.** Create `server/BlackIce.Server.LoadBalancing/Bots/IBotBehavior.cs`:

```csharp
namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>One step of bot decision-making, producing where the bot now is. Movement first; combat later.</summary>
public readonly record struct BotPositionUpdate(float X, float Y, float Z);

public interface IBotBehavior
{
    BotPositionUpdate Tick();
}
```

Create `server/BlackIce.Server.LoadBalancing/Bots/WanderBehavior.cs`:

```csharp
namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>A simple random-walk bounded to a radius around the spawn point. Deterministic with a seed.</summary>
public sealed class WanderBehavior : IBotBehavior
{
    private readonly float _ox, _oz, _radius;
    private readonly Random _rng;
    private float _x, _z;

    public WanderBehavior(float startX, float startZ, int? seed = null, float radius = 10f)
    {
        _ox = _x = startX; _oz = _z = startZ; _radius = radius;
        _rng = seed is int s ? new Random(s) : new Random();
    }

    public BotPositionUpdate Tick()
    {
        _x = Clamp(_x + (float)(_rng.NextDouble() * 2 - 1), _ox);
        _z = Clamp(_z + (float)(_rng.NextDouble() * 2 - 1), _oz);
        return new BotPositionUpdate(_x, 0f, _z);
    }

    private float Clamp(float v, float origin) => Math.Clamp(v, origin - _radius, origin + _radius);
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/Bots/IBotBehavior.cs server/BlackIce.Server.LoadBalancing/Bots/WanderBehavior.cs server/BlackIce.Server.Tests/Bots/WanderBehaviorTests.cs
git commit -m "feat(bots): IBotBehavior + bounded WanderBehavior (deterministic)"
```

---

## Task 4: BotManager — reserve actor, spawn, drive ticks

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/Bots/BotManager.cs`
- Test: `server/BlackIce.Server.Tests/Bots/BotManagerTests.cs`

**Context:** `BotManager` reserves a bot actor number from a HIGH range that never collides with real actors (real actors come from `Room.AddActor()` starting at 1; use a bot base like 10000+ so viewID blocks `10000*1000+…` never overlap real `actor*1000` blocks for realistic player counts). It spawns a `PlayerBot` into a `RoomSession` and, each tick, asks the behavior for a position and relays a 201 event for the bot. `Tick(session)` is called by the host off the listener maintenance cadence (the host wiring is Task 5; here it's directly unit-tested).

- [ ] **Step 1 — failing test:**

```csharp
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class BotManagerTests
{
    private static PeerConnection RealPeer(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>(); raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, u) => captured.Add((ev, u)); return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }
    private static RoomSession Session() =>
        new("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));

    [Fact]
    public void Spawned_bot_gets_a_high_non_colliding_actor_number()
    {
        var mgr = new BotManager();
        var bot = mgr.Spawn(Session(), new BotIdentityGenerator(seed: 1).Next());
        Assert.True(bot.Actor >= 10000, "bot actors live in a high range that can't collide with real actors");
    }

    [Fact]
    public void Tick_relays_an_unreliable_position_event_for_the_bot()
    {
        var session = Session();
        var human = RealPeer(out var raised); session.Join(1, human);
        var mgr = new BotManager();
        mgr.Spawn(session, new BotIdentityGenerator(seed: 1).Next());
        raised.Clear();

        mgr.Tick();

        // A position event (201) for the bot, sent unreliably (like real movement).
        Assert.Contains(raised, r => r.ev.Code == 201 && r.unreliable);
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement** `server/BlackIce.Server.LoadBalancing/Bots/BotManager.cs`:

```csharp
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// Owns the live bots: reserves non-colliding actor numbers, spawns them into a room session, and
/// drives their per-tick movement. Bot actors start at <see cref="BotActorBase"/> so their
/// viewID blocks (actor*1000) never overlap real players' blocks. Tick is driven off the host's
/// 1 Hz maintenance loop (single-threaded), so no locking is needed here.
/// </summary>
public sealed class BotManager
{
    public const int BotActorBase = 10000;
    private const byte EvPosition = 201, PData = 245;

    private int _nextBotActor = BotActorBase;
    private readonly List<(PlayerBot bot, RoomSession session, IBotBehavior behavior)> _bots = new();

    public PlayerBot Spawn(RoomSession session, BotIdentity identity, IBotBehavior? behavior = null)
    {
        var bot = new PlayerBot(_nextBotActor++, identity);
        bot.Spawn(session);
        _bots.Add((bot, session, behavior ?? new WanderBehavior(0, 0)));
        return bot;
    }

    /// <summary>Advances every bot one step and relays its new position (event 201, unreliable).</summary>
    public void Tick()
    {
        foreach (var (bot, session, behavior) in _bots)
        {
            var p = behavior.Tick();
            session.RelayFrom(bot.Actor, BuildPositionEvent(bot.ViewId, p), unreliable: true);
        }
    }

    /// <summary>
    /// Builds a minimal event-201 serialize batch moving the bot's avatar. The exact PUN serialize
    /// sub-stream layout is verified against the real client (interop review) before this is trusted
    /// live; the structure here mirrors the observed `[time, null, [viewId, false, null, pos, rot]]`.
    /// </summary>
    private static EventData BuildPositionEvent(int viewId, BotPositionUpdate p)
    {
        var pos = PunVector3(p.X, p.Y, p.Z);
        var rot = PunQuaternion(0, 0, 0, 1);
        var view = new object[] { viewId, false, null!, pos, rot };
        var batch = new object[] { System.Environment.TickCount, null!, view };
        return new EventData(EvPosition, new() { { PData, batch } });
    }

    private static PhotonCustomData PunVector3(float x, float y, float z)
    {
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(86, b);
    }

    private static PhotonCustomData PunQuaternion(float x, float y, float z, float w)
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(12), w);
        return new PhotonCustomData(81, b);
    }
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/Bots/BotManager.cs server/BlackIce.Server.Tests/Bots/BotManagerTests.cs
git commit -m "feat(bots): BotManager reserves actors, spawns bots, ticks wander movement"
```

---

## Task 5: Host wiring + console command + interop review

**Files:**
- Modify: `server/BlackIce.Server.Host/Program.cs` (construct BotManager, tick it off the maintenance loop, add a console `bot` command)
- Modify: `server/BlackIce.Server.Core/UdpListener.cs` OR Program console — wire `BotManager.Tick()` into the 1 Hz cadence.

- [ ] **Step 1 — dispatch photon-interop-reviewer** on `PlayerBot`'s instantiate (202) payload, the model-color custom type (67), and `BotManager`'s 201 position batch — confirm against the real DLL that the prefab/viewID/instantiation-data keys and the serialize-batch shape are what an unmodified client accepts (so a bot actually appears + moves). Fix findings; re-review. This is the gate before live.

- [ ] **Step 2 — console `bot` command:** in `Program.cs`'s console loop, add a `bot <realm>` command that spawns one bot into that realm's session via the BotManager, and wire `botManager.Tick()` to run once per maintenance interval (reuse the listener cadence or a dedicated 1 Hz timer on the Game listener's thread). Build + full test.

- [ ] **Step 3 — commit:**
```bash
git add server/BlackIce.Server.Host/Program.cs server/BlackIce.Server.Core/UdpListener.cs
git commit -m "feat(host): spawn + tick playerbots; console 'bot <realm>' command"
```

- [ ] **Step 4 — LIVE (user gate):** with one real client in a realm, run `bot <realm>` in the server console; a generated AI player should appear and wander. Flag in remember.md.

---

## Self-review
- Identity generation (free-form) → Task 1. ✓
- Join+identity+instantiate+refresh lifecycle → Task 2 (matches recon order). ✓
- Pluggable behavior + wander movement → Task 3. ✓
- Actor/viewID reservation (non-colliding) + tick-driven position relay → Task 4. ✓
- Wire-accuracy of 202/201/custom-types vs real client → Task 5 interop gate. ✓
- Placeholders: none; full code each step. ✓
- Type consistency: `BotIdentity(Name,ModelIndex,ModelColors,Level,Team)`; `PlayerBot(actor,identity)` with `.Actor/.ViewId/.Spawn(session)`; `IBotBehavior.Tick()→BotPositionUpdate`; `BotManager.Spawn(session,identity,behavior?)/.Tick()`; relays via existing `RoomSession.RelayFrom(int,EventData,bool)`. ✓
