# Phase 2a — Event Relay Substrate + Interceptor Seam — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Game server fan out a client's in-room events (RaiseEvent / property changes / join / leave) to the *other* actors in the same room, through a per-event interceptor pipeline whose default verdict is pass-through (pure relay). After this, movement, spawning, damage, and death replicate between two real clients with no per-event code.

**Architecture:** A new `RoomSession` (one per room) holds the live `PeerConnection` set and runs an ordered `IEventInterceptor` chain that returns Forward/Drop/Rewrite/Originate; `RoomSession` then delivers the resulting event to every *other* member. `GameServerHandler` registers/unregisters peers and routes inbound gameplay events into the session instead of dropping them. Authority interceptors and playerbots (Phases 2b/2c) plug into the same seam later — this plan ships only the substrate and a pass-through default.

**Tech Stack:** C# / .NET 8, xUnit. Builds on existing `BlackIce.Server.LoadBalancing` (`Room`/`RoomRegistry`, `GameServerHandler`), `BlackIce.Server.Core` (`PeerConnection`, `IOperationHandler`), and `BlackIce.Photon` (`EventData`, `OperationRequest`).

**Scope note:** This is sub-plan **2a** of the Phase 2 spec (`docs/superpowers/specs/2026-05-30-phase2-relay-authority-playerbots-design.md`). 2b (mod-free authority interceptors) and 2c (playerbots) get their own plans once 2a's interfaces are real and byte layouts are verified live.

---

## File structure

- Create `server/BlackIce.Server.LoadBalancing/RelayVerdict.cs` — the verdict type returned by interceptors.
- Create `server/BlackIce.Server.LoadBalancing/EventContext.cs` — decoded inbound event + sender actor + room handle passed to interceptors.
- Create `server/BlackIce.Server.LoadBalancing/IEventInterceptor.cs` — the seam interface + the default pass-through interceptor.
- Create `server/BlackIce.Server.LoadBalancing/RoomSession.cs` — per-room membership (`PeerConnection`s), interceptor chain, and fan-out.
- Modify `server/BlackIce.Server.LoadBalancing/RoomRegistry.cs` — give `Room` a `RoomSession`, or hold sessions in the registry (decided in Task 2).
- Modify `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs` — register peer on join, unregister on disconnect, route OpRaiseEvent gameplay events into the session.
- Create tests: `server/BlackIce.Server.Tests/RelayVerdictTests.cs`, `RoomSessionRelayTests.cs`, `InterceptorChainTests.cs`, `GameServerRelayTests.cs`.

The codec decoders named in the spec (DamagePacket, position stream, 202) are **not** in 2a — the pass-through relay forwards raw `EventData` without needing to understand its contents. Decoders arrive with 2b when authority needs to read payloads.

---

## Task 1: RelayVerdict — the interceptor result type

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/RelayVerdict.cs`
- Test: `server/BlackIce.Server.Tests/RelayVerdictTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RelayVerdictTests
{
    [Fact]
    public void Forward_carries_the_original_event_and_no_extras()
    {
        var ev = new EventData(200, new() { { 245, "x" } });
        var v = RelayVerdict.Forward(ev);
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
        Assert.Empty(v.Originated);
    }

    [Fact]
    public void Drop_carries_no_event()
    {
        var v = RelayVerdict.Drop();
        Assert.Equal(RelayAction.Drop, v.Action);
        Assert.Null(v.Event);
    }

    [Fact]
    public void Rewrite_replaces_the_forwarded_event()
    {
        var replacement = new EventData(200, new() { { 245, "clamped" } });
        var v = RelayVerdict.Rewrite(replacement);
        Assert.Equal(RelayAction.Rewrite, v.Action);
        Assert.Same(replacement, v.Event);
    }

    [Fact]
    public void Originate_forwards_original_plus_extra_events()
    {
        var ev = new EventData(200, new());
        var extra = new EventData(202, new());
        var v = RelayVerdict.Originate(ev, new[] { extra });
        Assert.Equal(RelayAction.Originate, v.Action);
        Assert.Same(ev, v.Event);
        Assert.Single(v.Originated);
        Assert.Same(extra, v.Originated[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RelayVerdictTests`
Expected: FAIL — `RelayVerdict`/`RelayAction` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `server/BlackIce.Server.LoadBalancing/RelayVerdict.cs`:

```csharp
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing;

/// <summary>What the relay should do with an inbound event after the interceptor chain runs.</summary>
public enum RelayAction { Forward, Drop, Rewrite, Originate }

/// <summary>
/// An interceptor's decision. <see cref="Forward"/> relays the event unchanged; <see cref="Drop"/>
/// swallows it; <see cref="Rewrite"/> relays a replacement; <see cref="Originate"/> relays the event
/// plus extra server-authored events (used by authority corrections and playerbots in later phases).
/// </summary>
public sealed class RelayVerdict
{
    public RelayAction Action { get; }
    /// <summary>The event to relay (null for <see cref="RelayAction.Drop"/>).</summary>
    public EventData? Event { get; }
    /// <summary>Extra events to relay after <see cref="Event"/>. Empty unless Originate.</summary>
    public IReadOnlyList<EventData> Originated { get; }

    private RelayVerdict(RelayAction action, EventData? ev, IReadOnlyList<EventData> originated)
    {
        Action = action; Event = ev; Originated = originated;
    }

    private static readonly EventData[] None = System.Array.Empty<EventData>();

    public static RelayVerdict Forward(EventData ev) => new(RelayAction.Forward, ev, None);
    public static RelayVerdict Drop() => new(RelayAction.Drop, null, None);
    public static RelayVerdict Rewrite(EventData replacement) => new(RelayAction.Rewrite, replacement, None);
    public static RelayVerdict Originate(EventData ev, IReadOnlyList<EventData> extras) =>
        new(RelayAction.Originate, ev, extras);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RelayVerdictTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/RelayVerdict.cs server/BlackIce.Server.Tests/RelayVerdictTests.cs
git commit -m "feat(lb): RelayVerdict — interceptor decision type (forward/drop/rewrite/originate)"
```

---

## Task 2: EventContext and IEventInterceptor (the seam)

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/EventContext.cs`
- Create: `server/BlackIce.Server.LoadBalancing/IEventInterceptor.cs`
- Test: `server/BlackIce.Server.Tests/InterceptorChainTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class InterceptorChainTests
{
    private static EventContext Ctx(EventData ev) => new(roomName: "co-op", senderActor: 1, ev);

    [Fact]
    public void Passthrough_interceptor_forwards_unchanged()
    {
        var ev = new EventData(200, new());
        var v = new PassthroughInterceptor().Intercept(Ctx(ev));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
    }

    [Fact]
    public void Chain_runs_in_order_and_stops_at_first_non_forward()
    {
        var ev = new EventData(200, new());
        var chain = new InterceptorChain(new IEventInterceptor[]
        {
            new PassthroughInterceptor(),
            new DropEverythingInterceptor(),     // test stub below
            new ThrowingInterceptor(),           // must NOT run (chain stopped at drop)
        });
        var v = chain.Run(Ctx(ev));
        Assert.Equal(RelayAction.Drop, v.Action);
    }

    [Fact]
    public void A_throwing_interceptor_is_caught_and_treated_as_forward()
    {
        var ev = new EventData(200, new());
        var chain = new InterceptorChain(new IEventInterceptor[] { new ThrowingInterceptor() });
        var v = chain.Run(Ctx(ev));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
    }

    private sealed class DropEverythingInterceptor : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Drop();
    }
    private sealed class ThrowingInterceptor : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => throw new System.InvalidOperationException("boom");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter InterceptorChainTests`
Expected: FAIL — `EventContext`, `IEventInterceptor`, `PassthroughInterceptor`, `InterceptorChain` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `server/BlackIce.Server.LoadBalancing/EventContext.cs`:

```csharp
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// The decoded inbound event handed to interceptors: which room it is in, which actor sent it, and
/// the event itself. Phase 2b adds richer classification (RPC method, decoded payloads); 2a keeps it
/// to the raw event so the pass-through relay needs no payload understanding.
/// </summary>
public sealed class EventContext
{
    public string RoomName { get; }
    public int SenderActor { get; }
    public EventData Event { get; }

    public EventContext(string roomName, int senderActor, EventData ev)
    {
        RoomName = roomName; SenderActor = senderActor; Event = ev;
    }
}
```

Create `server/BlackIce.Server.LoadBalancing/IEventInterceptor.cs`:

```csharp
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// One authority/relay decision over an inbound in-room event. The default chain is a single
/// <see cref="PassthroughInterceptor"/> (pure relay). Authority interceptors (Phase 2b) and the
/// playerbot origination path (2c) plug in here.
/// </summary>
public interface IEventInterceptor
{
    RelayVerdict Intercept(EventContext ctx);
}

/// <summary>The default: relay every event unchanged.</summary>
public sealed class PassthroughInterceptor : IEventInterceptor
{
    public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Forward(ctx.Event);
}

/// <summary>
/// Runs interceptors in order, returning the first non-Forward verdict (Forward means "no opinion,
/// keep going"). If the whole chain forwards, the original event is forwarded. A throwing interceptor
/// is caught, logged, and skipped (treated as Forward) so an authority bug never drops a player.
/// </summary>
public sealed class InterceptorChain
{
    private readonly IEventInterceptor[] _interceptors;
    public InterceptorChain(IEventInterceptor[] interceptors) => _interceptors = interceptors;

    public RelayVerdict Run(EventContext ctx)
    {
        foreach (var i in _interceptors)
        {
            RelayVerdict v;
            try { v = i.Intercept(ctx); }
            catch (System.Exception ex)
            {
                Log.Exception("Relay", $"interceptor {i.GetType().Name} threw on event {ctx.Event.Code}", ex);
                continue;   // treat as Forward / no opinion
            }
            if (v.Action != RelayAction.Forward) return v;
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter InterceptorChainTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/EventContext.cs server/BlackIce.Server.LoadBalancing/IEventInterceptor.cs server/BlackIce.Server.Tests/InterceptorChainTests.cs
git commit -m "feat(lb): IEventInterceptor seam + chain (pass-through default, throw-safe)"
```

---

## Task 3: RoomSession — membership + fan-out

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/RoomSession.cs`
- Test: `server/BlackIce.Server.Tests/RoomSessionRelayTests.cs`

**Context:** `PeerConnection` (in `BlackIce.Server.Core`) exposes `public void RaiseEvent(EventData ev)` and a per-peer `object? Tag`. It also exposes `IPEndPoint Remote`. `RoomSession` needs to map an actor number to the `PeerConnection` that owns it, deliver to *other* actors, and run the interceptor chain. The session is driven from the single-threaded UDP listener loop, so a simple lock around membership is sufficient (matches the existing `Room` locking style).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RoomSessionRelayTests
{
    // Minimal fake peer that records what was raised to it.
    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        // PeerConnection's send callback is invoked for control commands; RaiseEvent wraps an app
        // message. We capture via a hook seam: see Step 3 note about RaiseEventForTest.
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0),
                                   new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;   // test-only hook added in Step 3
        return p;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Event_is_relayed_to_other_actors_not_the_sender()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out var aRaised); session.Join(actor: 1, a);
        var b = Peer(out var bRaised); session.Join(actor: 2, b);

        var ev = new EventData(200, new() { { 245, "hello" } });
        session.RelayFrom(senderActor: 1, ev);

        Assert.Empty(aRaised);             // sender does not receive its own event
        Assert.Single(bRaised);            // the other actor does
        Assert.Equal(200, bRaised[0].Code);
    }

    [Fact]
    public void A_left_actor_stops_receiving()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);
        session.Leave(2);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Empty(bRaised);
    }

    [Fact]
    public void Drop_verdict_relays_nothing()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new DropAll() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Empty(bRaised);
    }

    [Fact]
    public void Originate_relays_the_event_and_the_extras_to_others()
    {
        var extra = new EventData(202, new());
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new OriginateExtra(extra) }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()));
        Assert.Equal(2, bRaised.Count);
        Assert.Equal(200, bRaised[0].Code);
        Assert.Equal(202, bRaised[1].Code);
    }

    private sealed class DropAll : IEventInterceptor
    {
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Drop();
    }
    private sealed class OriginateExtra : IEventInterceptor
    {
        private readonly EventData _extra;
        public OriginateExtra(EventData extra) => _extra = extra;
        public RelayVerdict Intercept(EventContext ctx) => RelayVerdict.Originate(ctx.Event, new[] { _extra });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RoomSessionRelayTests`
Expected: FAIL — `RoomSession`, `PeerConnection.OnRaised` do not exist.

- [ ] **Step 3: Add the test-only raise hook to PeerConnection**

In `server/BlackIce.Server.Core/PeerConnection.cs`, change `RaiseEvent` to notify an optional hook so tests can observe relays without a socket. Find:

```csharp
    public void RaiseEvent(EventData ev)
    {
        Log.Info(_role, $"{Remote} -> raise {PhotonNames.Event(ev.Code)} [{PhotonNames.Params(ev.Parameters)}]");
        SendRaw(WireMessage.EventMessage(ev));
    }
```

Replace with:

```csharp
    /// <summary>Test-only observation hook: when set, every raised event is also handed here.
    /// Production leaves this null; it exists so relay tests can assert fan-out without a socket.</summary>
    internal System.Action<EventData>? OnRaised { get; set; }

    public void RaiseEvent(EventData ev)
    {
        Log.Info(_role, $"{Remote} -> raise {PhotonNames.Event(ev.Code)} [{PhotonNames.Params(ev.Parameters)}]");
        OnRaised?.Invoke(ev);
        SendRaw(WireMessage.EventMessage(ev));
    }
```

> Note: `internal` is visible to the test project only if InternalsVisibleTo is set. The test project is a separate assembly with no InternalsVisibleTo, so make `OnRaised` **public** instead of internal (matching the project's existing choice for `DatabaseOptions.AnchorSqliteFile`). Use:
> `public System.Action<EventData>? OnRaised { get; set; }`

- [ ] **Step 4: Write minimal RoomSession implementation**

Create `server/BlackIce.Server.LoadBalancing/RoomSession.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Per-room relay: holds the connected peers by actor number, runs the interceptor chain over an
/// inbound event, and fans the resulting event(s) out to every OTHER actor in the room. Driven from
/// the single-threaded UDP listener loop; a lock guards membership for safety against maintenance.
/// </summary>
public sealed class RoomSession
{
    private readonly object _gate = new();
    private readonly Dictionary<int, PeerConnection> _members = new();
    private readonly InterceptorChain _chain;

    public string RoomName { get; }

    public RoomSession(string roomName, InterceptorChain chain)
    {
        RoomName = roomName; _chain = chain;
    }

    public void Join(int actor, PeerConnection peer) { lock (_gate) _members[actor] = peer; }
    public void Leave(int actor) { lock (_gate) _members.Remove(actor); }
    public int Count { get { lock (_gate) return _members.Count; } }

    /// <summary>Runs the interceptor chain over <paramref name="ev"/> and fans the verdict out to
    /// every actor except <paramref name="senderActor"/>.</summary>
    public void RelayFrom(int senderActor, EventData ev)
    {
        var verdict = _chain.Run(new EventContext(RoomName, senderActor, ev));
        if (verdict.Action == RelayAction.Drop) return;

        // Snapshot recipients under the lock, then send outside it (sending may take time / log).
        List<PeerConnection> recipients;
        lock (_gate)
        {
            recipients = new List<PeerConnection>(_members.Count);
            foreach (var (actor, peer) in _members)
                if (actor != senderActor) recipients.Add(peer);
        }

        foreach (var peer in recipients)
        {
            if (verdict.Event is not null) peer.RaiseEvent(verdict.Event);
            foreach (var extra in verdict.Originated) peer.RaiseEvent(extra);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RoomSessionRelayTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/RoomSession.cs server/BlackIce.Server.Core/PeerConnection.cs server/BlackIce.Server.Tests/RoomSessionRelayTests.cs
git commit -m "feat(lb): RoomSession — per-room peer membership + interceptor-driven fan-out"
```

---

## Task 4: Hold a RoomSession per room in the registry

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/RoomRegistry.cs`
- Test: `server/BlackIce.Server.Tests/RoomSessionRegistryTests.cs` (create)

**Context:** `RoomRegistry.GetOrCreate(string)` returns a `Room`. We add a parallel `Session(string)` accessor that lazily creates one `RoomSession` per room, wired with the default pass-through chain. (2b will replace the chain factory with one that includes authority interceptors.)

- [ ] **Step 1: Write the failing test**

```csharp
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RoomSessionRegistryTests
{
    [Fact]
    public void Session_is_created_once_per_room_name()
    {
        var reg = new RoomRegistry();
        var s1 = reg.Session("co-op");
        var s2 = reg.Session("co-op");
        Assert.Same(s1, s2);
        Assert.Equal("co-op", s1.RoomName);
    }

    [Fact]
    public void Different_rooms_get_different_sessions()
    {
        var reg = new RoomRegistry();
        Assert.NotSame(reg.Session("co-op"), reg.Session("pvp"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RoomSessionRegistryTests`
Expected: FAIL — `RoomRegistry.Session` does not exist.

- [ ] **Step 3: Implement the Session accessor**

In `server/BlackIce.Server.LoadBalancing/RoomRegistry.cs`, the `RoomRegistry` class currently is:

```csharp
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();

    public Room GetOrCreate(string name) => _rooms.GetOrAdd(name, n => new Room { Name = n });
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Room> All => (IReadOnlyCollection<Room>)_rooms.Values;
}
```

Replace it with (adds a sessions map + default chain factory):

```csharp
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, RoomSession> _sessions = new();

    public Room GetOrCreate(string name) => _rooms.GetOrAdd(name, n => new Room { Name = n });
    public Room? Find(string name) => _rooms.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Room> All => (IReadOnlyCollection<Room>)_rooms.Values;

    /// <summary>The relay session for a room, created on first use with the default (pass-through)
    /// interceptor chain. Phase 2b swaps in a chain that includes authority interceptors.</summary>
    public RoomSession Session(string name) =>
        _sessions.GetOrAdd(name, n => new RoomSession(n, new InterceptorChain(
            new IEventInterceptor[] { new PassthroughInterceptor() })));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter RoomSessionRegistryTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/RoomRegistry.cs server/BlackIce.Server.Tests/RoomSessionRegistryTests.cs
git commit -m "feat(lb): RoomRegistry.Session — one relay session per room (default pass-through)"
```

---

## Task 5: Wire the relay into GameServerHandler

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/GameServerRelayTests.cs` (create)

**Context:** Today `OnOperationRequest`'s `OpRaiseEvent` case only handles `/motd` and otherwise drops the event. We make it: (1) on successful join, register the peer into the room's session under its actor number and store the actor on `peer.Tag`; (2) on disconnect, unregister; (3) on `OpRaiseEvent`, after the chat-command check returns null (not a command), route the event into the session for relay. We must preserve `peer.Tag` for the room name (used by SetProperties + chat). To carry both room name and actor, change `peer.Tag` to a small record.

**Current `peer.Tag` usage:** set to room name string on join (line ~68); read as `peer.Tag as string` in `SetProperties` and `TryHandleChatCommand`. We introduce a `PeerRoomState` record holding `RoomName` and `Actor`, update the two readers to pull `.RoomName`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class GameServerRelayTests
{
    private static (GameServerHandler h, RoomRegistry reg, TestDb db) NewHandler()
    {
        var db = new TestDb();
        db.Context.Realms.Add(new Realm { Name = "co-op", IsEnabled = true });
        db.Context.SaveChanges();
        var reg = new RoomRegistry();
        var h = new GameServerHandler("s", reg, allowAnonymousLan: true, realms: new RealmService(db.Context));
        return (h, reg, db);
    }

    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static OperationRequest Join() => new(226, new() { { 255, "co-op" } });
    private static OperationRequest GameplayRpc() => new(253, new()
    {
        { 244, (byte)200 },                       // PUN RPC event code
        { 245, new Dictionary<object, object> { { (byte)0, 2001 }, { (byte)5, (byte)73 },
                 { (byte)4, new object[] { 1.0 } } } },   // a non-/motd gameplay RPC
    });

    [Fact]
    public void Gameplay_rpc_from_one_actor_is_relayed_to_the_other()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());     // a becomes actor 1
            h.OnOperationRequest(b, Join());     // b becomes actor 2
            aRaised.Clear(); bRaised.Clear();    // discard the join events

            h.OnOperationRequest(a, GameplayRpc());

            Assert.Empty(aRaised);               // sender doesn't get its own RPC back
            Assert.Single(bRaised);              // the other actor receives it
            Assert.Equal(200, bRaised[0].Code);
        }
    }

    [Fact]
    public void Slash_motd_is_still_intercepted_not_relayed()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            aRaised.Clear(); bRaised.Clear();

            var motd = new OperationRequest(253, new()
            {
                { 244, (byte)200 },
                { 245, new Dictionary<object, object> { { (byte)5, (byte)7 },
                         { (byte)4, new object[] { "/motd" } } } },
            });
            h.OnOperationRequest(a, motd);

            Assert.Single(aRaised);              // the ServerMessage reply goes to the sender
            Assert.Equal(199, aRaised[0].Code);
            Assert.Empty(bRaised);               // and is NOT relayed to others
        }
    }

    [Fact]
    public void A_disconnected_actor_no_longer_receives_relayed_events()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out _); var b = Peer(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            h.OnDisconnect(b);
            bRaised.Clear();

            h.OnOperationRequest(a, GameplayRpc());
            Assert.Empty(bRaised);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter GameServerRelayTests`
Expected: FAIL — gameplay RPC is not relayed (b receives nothing); compile errors if `PeerRoomState` not yet referenced.

- [ ] **Step 3: Add PeerRoomState and update Tag usage**

In `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`, add this record near the top of the namespace (after the `using`s, before the class):

```csharp
/// <summary>Per-peer in-room state stashed on PeerConnection.Tag once a peer joins a room.</summary>
public sealed record PeerRoomState(string RoomName, int Actor);
```

- [ ] **Step 4: Capture the actor number in EnterRoom and register the peer**

In `OnOperationRequest`, the join case currently is:

```csharp
            case OpCreateGame:
            case OpJoinGame:
                var (response, join) = EnterRoom(request, ExtractJoinPassword(request));
                peer.SendResponse(response);
                if (response.ReturnCode == 0)
                {
                    peer.Tag = request.Parameters.TryGetValue(PRoomName, out var rn) ? rn.ToString() : null;
                    peer.RaiseEvent(join);
                }
                break;
```

Replace it with (registers the peer into the session; reads the assigned actor from the join event's ActorNr param 254):

```csharp
            case OpCreateGame:
            case OpJoinGame:
                var (response, join) = EnterRoom(request, ExtractJoinPassword(request));
                peer.SendResponse(response);
                if (response.ReturnCode == 0)
                {
                    var roomName = request.Parameters.TryGetValue(PRoomName, out var rn) ? rn.ToString()! : "room";
                    var actor = join.Parameters.TryGetValue(PActorNr, out var an) && an is int ai ? ai : 0;
                    peer.Tag = new PeerRoomState(roomName, actor);
                    _registry.Session(roomName).Join(actor, peer);
                    peer.RaiseEvent(join);
                }
                break;
```

- [ ] **Step 5: Relay non-command gameplay events**

In `OnOperationRequest`, the `OpRaiseEvent` case currently is:

```csharp
            case OpRaiseEvent:
                var reply = TryHandleChatCommand(peer.Tag as string, request);
                if (reply is not null) peer.RaiseEvent(reply);   // command handled; not relayed
                ...
                break;
```

Replace it with:

```csharp
            case OpRaiseEvent:
                var state = peer.Tag as PeerRoomState;
                var reply = TryHandleChatCommand(state?.RoomName, request);
                if (reply is not null)
                {
                    peer.RaiseEvent(reply);   // command handled; not relayed
                }
                else if (state is not null
                         && request.Parameters.TryGetValue(PEventCode, out var ecRaw) && ecRaw is byte ec
                         && request.Parameters.TryGetValue(PData, out var data))
                {
                    // Not a server command: relay this gameplay event to the other actors in the room.
                    _registry.Session(state.RoomName).RelayFrom(state.Actor, new EventData(ec, ToParamTable(data)));
                }
                break;
```

- [ ] **Step 6: Add the param-table helper and fix the other Tag readers**

Still in `GameServerHandler.cs`, add this helper method to the class (converts the RaiseEvent payload into an `EventData` parameter table; the client sends the event content under PData(245), and Photon clients read a raised event's content the same way):

```csharp
    /// <summary>Wraps a RaiseEvent's data payload as an event parameter table (content under PData).</summary>
    private static Dictionary<byte, object> ToParamTable(object data) => new() { { PData, data } };
```

Then update the two existing readers of `peer.Tag as string`:

- In the `OpSetProperties` case: `peer.SendResponse(SetProperties(peer.Tag as string, request));`
  becomes `peer.SendResponse(SetProperties((peer.Tag as PeerRoomState)?.RoomName, request));`

- `SetProperties(string? roomName, ...)` and `TryHandleChatCommand(string? roomName, ...)` keep their
  `string?` signatures — only the call sites change (above), so their bodies are untouched.

- [ ] **Step 7: Unregister on disconnect**

In `GameServerHandler.cs`, `OnDisconnect` is currently `public void OnDisconnect(PeerConnection peer) { }`. Replace with:

```csharp
    public void OnDisconnect(PeerConnection peer)
    {
        if (peer.Tag is PeerRoomState state)
            _registry.Session(state.RoomName).Leave(state.Actor);
    }
```

- [ ] **Step 8: Run the new tests + full suite**

Run: `dotnet test server/BlackIce.Server.sln`
Expected: PASS — `GameServerRelayTests` (3) green, and all pre-existing tests still pass (the `/motd` test and `ChatCommandTests` continue to work because `TryHandleChatCommand` is unchanged and still receives the room name).

- [ ] **Step 9: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/GameServerHandler.cs server/BlackIce.Server.Tests/GameServerRelayTests.cs
git commit -m "feat(lb): relay non-command gameplay events to other actors in the room"
```

---

## Task 6: Relay join/leave so actors see each other arrive and depart

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/GameServerRelayTests.cs` (add)

**Context:** Today the join event (255) is raised only to the *joining* peer (`peer.RaiseEvent(join)`), so existing players never learn a new actor arrived, and the newcomer never learns who is already present. Real Photon raises a join event to the others (with the updated actor list) and the newcomer gets the existing actor list in its own join. For 2a we add: when actor J joins, relay a join event (255, ActorNr=J, ActorList=all) to the *other* actors. Leave (event 254) is raised to others on disconnect.

- [ ] **Step 1: Write the failing test (add to GameServerRelayTests)**

```csharp
    [Fact]
    public void Existing_actors_are_notified_when_a_new_actor_joins()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised);
            h.OnOperationRequest(a, Join());     // actor 1
            aRaised.Clear();

            var b = Peer(out _);
            h.OnOperationRequest(b, Join());     // actor 2 joins

            // a (already present) should receive a join event announcing actor 2
            Assert.Contains(aRaised, e => e.Code == 255
                && e.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 2);
        }
    }

    [Fact]
    public void Remaining_actors_are_notified_when_an_actor_leaves()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out var aRaised);
            var b = Peer(out _);
            h.OnOperationRequest(a, Join());     // actor 1
            h.OnOperationRequest(b, Join());     // actor 2
            aRaised.Clear();

            h.OnDisconnect(b);

            Assert.Contains(aRaised, e => e.Code == 254
                && e.Parameters.TryGetValue(254, out var nr) && nr is int i && i == 2);
        }
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter GameServerRelayTests`
Expected: FAIL — no join/leave notification is relayed to others.

- [ ] **Step 3: Relay the join to others**

In `GameServerHandler.cs`, in the join case (the block added in Task 5), after `_registry.Session(roomName).Join(actor, peer);` and before `peer.RaiseEvent(join);`, add a relay of the join to the others. The join `EventData` already carries ActorNr(254) and ActorList(252); reuse it:

```csharp
                    _registry.Session(roomName).Join(actor, peer);
                    // Tell the already-present actors that this actor arrived (event 255).
                    _registry.Session(roomName).RelayFrom(actor, join);
                    peer.RaiseEvent(join);
```

> `RelayFrom` excludes the sender (actor), so only the others receive it — exactly right.

- [ ] **Step 4: Relay a leave event on disconnect**

In `GameServerHandler.cs`, add a leave-event constant near the other `Ev*` constants:

```csharp
    private const byte EvLeave = 254;
```

Update `OnDisconnect` (from Task 5) to notify the others before removing the peer:

```csharp
    public void OnDisconnect(PeerConnection peer)
    {
        if (peer.Tag is not PeerRoomState state) return;
        var session = _registry.Session(state.RoomName);
        // Notify remaining actors that this actor left (event 254, ActorNr = leaver).
        session.RelayFrom(state.Actor, new EventData(EvLeave, new() { { PActorNr, state.Actor } }));
        session.Leave(state.Actor);
    }
```

- [ ] **Step 5: Run the tests + full suite**

Run: `dotnet test server/BlackIce.Server.sln`
Expected: PASS — the two new tests green, all prior tests still pass.

- [ ] **Step 6: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/GameServerHandler.cs server/BlackIce.Server.Tests/GameServerRelayTests.cs
git commit -m "feat(lb): relay join (255) and leave (254) so actors see each other arrive/depart"
```

---

## Task 7: Full-suite verification + interop sanity

**Files:** none (verification only)

- [ ] **Step 1: Run the entire solution test suite**

Run: `dotnet test server/BlackIce.Server.sln`
Expected: PASS — all tests including the new relay tests; no regressions in Photon oracle / MOTD / keepalive / SetProperties suites.

- [ ] **Step 2: Build the whole solution clean**

Run: `dotnet build server/BlackIce.Server.sln`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Update the handoff note**

Append to `.remember/remember.md` a short "Phase 2a relay substrate complete" entry: what shipped (RoomSession + interceptor seam + fan-out of gameplay/join/leave), that authority is still pass-through, and that the live multiplayer check (two clients seeing each other) is the next gate before 2b.

- [ ] **Step 4: Commit**

```bash
git add .remember/remember.md
git commit -m "docs(remember): Phase 2a relay substrate complete; live two-client check is next"
```

---

## Live verification (manual, after the plan executes)

Not a code task, but the real acceptance test: run the server (`dotnet run --project server/BlackIce.Server.Host -c Debug -- 127.0.0.1 --trace`) and connect **two** game clients to the same realm. Each should see the other's avatar spawn and move, and (entering combat) see damage/death replicate — all via the pass-through relay, no per-event code. The `--trace` log will show `RelayFrom` fan-out. This is the gate before starting Phase 2b (authority interceptors).

---

## Self-review

**Spec coverage (2a portion):**
- Relay substrate + fan-out to other actors → Tasks 3, 5. ✓
- Interceptor seam (Forward/Drop/Rewrite/Originate), pass-through default, throw→Forward → Tasks 1, 2. ✓
- One session per room → Task 4. ✓
- Join/leave replication → Task 6. ✓
- Movement/spawn/damage/death "for free" → emergent from Task 5's gameplay relay (verified live, not unit-tested, since it depends on the real client). ✓
- Codec decoders (DamagePacket/201/202) → intentionally **deferred to 2b** (pass-through needs no payload decode); noted in File Structure. ✓
- Authority interceptors, playerbots → **out of scope for 2a**, separate plans. ✓

**Placeholder scan:** no TBD/TODO; every code step shows complete code and exact commands. ✓

**Type consistency:** `RelayVerdict`/`RelayAction`, `EventContext(roomName, senderActor, ev)`, `IEventInterceptor.Intercept`, `InterceptorChain.Run`, `RoomSession(roomName, chain)` with `Join(actor, peer)`/`Leave(actor)`/`RelayFrom(senderActor, ev)`, `RoomRegistry.Session(name)`, `PeerRoomState(RoomName, Actor)`, `PeerConnection.OnRaised` (public) — all consistent across tasks. The `peer.Tag` migration from `string` to `PeerRoomState` updates every reader (Task 5 Step 6). ✓
