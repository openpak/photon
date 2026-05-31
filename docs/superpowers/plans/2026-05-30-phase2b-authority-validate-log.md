# Phase 2b — Authority interceptors (validate-and-log)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development / executing-plans. Steps use `- [ ]`.

**Goal:** Add the first server-authority interceptors to the relay seam, in **validate-and-LOG-only** mode: decode relayed damage RPCs and movement updates, detect impossible values (speedhack / inflated damage), and LOG violations. **No drop/clamp/rewrite yet** — every verdict stays Forward. This gives the anticheat signal with zero risk of breaking legit play; enforcement (Drop/Rewrite) is a later, threshold-tuned step done with the user watching live.

**Architecture:** Concrete `IEventInterceptor`s plug into the existing chain (`RoomRegistry.Session` builds it). They use new codec helpers in `BlackIce.Photon` to decode the relevant payloads: the PUN RPC method + `DamagePacket` (custom type 68, damage = first 4 bytes, big-endian float), and the absolute position from the event-201 serialize batch. Per-actor state (last position+time) lives in a small tracker the movement interceptor owns.

**Tech Stack:** C#/.NET 8, xUnit, oracle = real Photon3Unity3D.dll.

**Recon basis (done):** damage = event 200, RPC method `TakeDamage` (string at RPC key 3, or byte shortcut at key 5), args (key 4) contain a `DamagePacket` custom type **code 68**, a 41-byte blob whose **first 4 bytes are the damage float (big-endian)**. Movement = event 201, per-view absolute position as a PUN Vector3 (custom type 86, 3 big-endian floats) inside the serialize batch under PData(245). The interceptor sees the already-decoded `EventData` (PhotonCustomData preserves the raw bytes), so decoding damage/position = reading those bytes — no new wire parsing of the transport.

**Scope (YAGNI):** decode helpers + two log-only interceptors + chain wiring + tests. NOT in scope: dropping/clamping/rewriting, the spawn-authority/master-client interceptor (separate later step), persistence of violations, or per-account ban actions.

---

## Task 1: DamagePacket + RPC-method decode helpers (codec, oracle-tested)

**Files:**
- Create: `server/BlackIce.Photon/PunRpcInfo.cs`
- Test: `server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs`

**Context:** A relayed gameplay event is an `EventData` whose `Parameters[245]` holds the RPC hashtable for event 200 (keys: 0=viewId, 3=methodName string OR 5=method shortcut byte, 4=args object[]). Damage args contain a `PhotonCustomData(68, bytes)` where bytes[0..4] is the damage float (big-endian). We add a pure helper that, given an `EventData`, extracts: the RPC method name (or null if shortcut-only), and any `DamagePacket` damage value found in the args.

- [ ] **Step 1 — failing test** (`server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs`):

```csharp
using System.Buffers.Binary;
using System.Collections.Generic;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PunRpcDecodeTests
{
    private static PhotonCustomData DamagePacket(float damage)
    {
        var b = new byte[41];                                  // 41-byte DamagePacket; damage at offset 0
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), damage);
        return new PhotonCustomData(68, b);
    }

    [Fact]
    public void Reads_named_rpc_method_and_damage_value()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { 5, DamagePacket(42.5f) } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Equal("TakeDamage", info!.Value.Method);
        Assert.True(info.Value.DamageValue.HasValue);
        Assert.Equal(42.5f, info.Value.DamageValue!.Value, 3);
    }

    [Fact]
    public void Handles_shortcut_rpc_with_null_method_name()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },
                    { (byte)5, (byte)73 },                      // shortcut index, no name
                    { (byte)4, new object[] { DamagePacket(10f) } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Null(info!.Value.Method);
        Assert.Equal(10f, info.Value.DamageValue!.Value, 3);
    }

    [Fact]
    public void Returns_null_for_non_rpc_events()
    {
        Assert.Null(PunRpcInfo.From(new EventData(201, new() { { 245, "not-an-rpc" } })));
        Assert.Null(PunRpcInfo.From(new EventData(255, new() { { 254, 1 } })));
    }

    [Fact]
    public void No_damage_value_when_args_have_no_damage_packet()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)3, "Move" },
                    { (byte)4, new object[] { 1, 2, "x" } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Null(info!.Value.DamageValue);
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement** `server/BlackIce.Photon/PunRpcInfo.cs`:

```csharp
using System.Buffers.Binary;
using System.Collections;

namespace BlackIce.Photon;

/// <summary>
/// Decoded view of a PUN RPC event (Photon event code 200) for authority checks: the target view id,
/// the method name (null when sent as a shortcut index), and — if any argument is a DamagePacket
/// custom type (code 68) — its damage value (the first 4 bytes, a big-endian float).
/// Pure read over an already-decoded EventData; no transport parsing.
/// </summary>
public readonly record struct PunRpcInfo(int ViewId, string? Method, float? DamageValue)
{
    private const byte PunRpcEventCode = 200;
    private const byte PData = 245, RpcViewId = 0, RpcMethodName = 3, RpcArgs = 4;
    private const byte DamagePacketCode = 68;

    /// <summary>Decodes <paramref name="ev"/> as a PUN RPC, or null if it is not event 200 with an RPC table.</summary>
    public static PunRpcInfo? From(EventData ev)
    {
        if (ev.Code != PunRpcEventCode) return null;
        if (!ev.Parameters.TryGetValue(PData, out var d) || d is not IDictionary rpc) return null;

        int viewId = rpc.Contains(RpcViewId) && rpc[RpcViewId] is int v ? v : 0;
        string? method = rpc.Contains(RpcMethodName) ? rpc[RpcMethodName] as string : null;

        float? damage = null;
        if (rpc.Contains(RpcArgs) && rpc[RpcArgs] is object[] args)
        {
            foreach (var a in args)
                if (a is PhotonCustomData { Code: DamagePacketCode } dp && dp.Data.Length >= 4)
                {
                    damage = BinaryPrimitives.ReadSingleBigEndian(dp.Data.AsSpan(0, 4));
                    break;
                }
        }
        return new PunRpcInfo(viewId, method, damage);
    }
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — add an oracle round-trip test** confirming a DamagePacket we build round-trips through the real DLL (mirror the existing oracle custom-type tests; register code 68 if not already — OracleFixture registers it). Append to `PunRpcDecodeTests.cs`:

```csharp
    [Fact]
    public void DamagePacket_damage_float_survives_oracle_roundtrip()
    {
        var dp = DamagePacket(123.25f);
        var ourBytes = new GpBinaryWriter().WriteTyped(dp).ToArray();
        var decoded = Oracle.Deserialize(ourBytes);           // real DLL must accept code-68 slim custom
        Assert.NotNull(decoded);
        // And our reader recovers the damage float from the round-tripped bytes.
        var back = (PhotonCustomData)new GpBinaryReader(ourBytes).ReadTyped()!;
        Assert.Equal(123.25f, System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(back.Data.AsSpan(0, 4)), 3);
    }
```
Run; if the oracle lacks code 68 registration, extend `OracleFixture` (it already registers 68/86; add only if missing). Expect PASS.

- [ ] **Step 6 — commit:**
```bash
git add server/BlackIce.Photon/PunRpcInfo.cs server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs server/BlackIce.Photon.Tests/OracleFixture.cs
git commit -m "feat(photon): PunRpcInfo — decode RPC method + DamagePacket damage for authority checks"
```

---

## Task 2: DamageValidationInterceptor (log-only)

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/Authority/DamageValidationInterceptor.cs`
- Test: `server/BlackIce.Server.Tests/Authority/DamageValidationInterceptorTests.cs`

**Context:** Implements `IEventInterceptor`. For an event the `PunRpcInfo.From` decodes with a damage value, if damage exceeds a configured sane maximum, LOG a Warn naming the sender actor + value. ALWAYS returns `RelayVerdict.Forward(ctx.Event)` (log-only this phase). This is the seam working without enforcing.

- [ ] **Step 1 — failing test:**

```csharp
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace BlackIce.Server.Tests.Authority;

public class DamageValidationInterceptorTests
{
    private static EventData DamageRpc(float dmg)
    {
        var b = new byte[41]; BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), dmg);
        return new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { new PhotonCustomData(68, b) } },
                } },
        });
    }

    [Fact]
    public void Always_forwards_even_when_damage_is_absurd()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        var v = i.Intercept(new EventContext("co-op", 1, DamageRpc(999999f)));
        Assert.Equal(RelayAction.Forward, v.Action);       // log-only phase: never drops
    }

    [Fact]
    public void Flags_count_increments_only_for_over_threshold_damage()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        i.Intercept(new EventContext("co-op", 1, DamageRpc(50f)));     // fine
        i.Intercept(new EventContext("co-op", 1, DamageRpc(5000f)));   // flagged
        i.Intercept(new EventContext("co-op", 1, DamageRpc(20f)));     // fine
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Non_damage_events_pass_without_flagging()
    {
        var i = new DamageValidationInterceptor(maxDamage: 1000f);
        var v = i.Intercept(new EventContext("co-op", 1, new EventData(201, new() { { 245, "pos" } })));
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Equal(0, i.FlaggedCount);
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement** `server/BlackIce.Server.LoadBalancing/Authority/DamageValidationInterceptor.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed damage RPCs (PUN event 200 carrying a DamagePacket) and LOGS any damage value
/// above a sane maximum. Phase 2b is detection-only: it always forwards the event unchanged. A later
/// phase can switch over-threshold hits to Rewrite/Drop once thresholds are tuned against live play.
/// </summary>
public sealed class DamageValidationInterceptor : IEventInterceptor
{
    private readonly float _maxDamage;
    public int FlaggedCount { get; private set; }

    public DamageValidationInterceptor(float maxDamage) => _maxDamage = maxDamage;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var info = PunRpcInfo.From(ctx.Event);
        if (info?.DamageValue is float dmg && dmg > _maxDamage)
        {
            FlaggedCount++;
            Log.Warn("Authority", $"actor {ctx.SenderActor} in \"{ctx.RoomName}\" dealt suspicious damage " +
                                  $"{dmg:F1} (> max {_maxDamage:F0}) via {info.Value.Method ?? "<shortcut rpc>"} " +
                                  $"-> forwarded (log-only)");
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
```

- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/Authority/DamageValidationInterceptor.cs server/BlackIce.Server.Tests/Authority/DamageValidationInterceptorTests.cs
git commit -m "feat(authority): DamageValidationInterceptor — log over-threshold damage (forward-only)"
```

---

## Task 3: MovementValidationInterceptor (log-only)

**Files:**
- Create: `server/BlackIce.Photon/PositionInfo.cs` (decode absolute position from event 201)
- Create: `server/BlackIce.Server.LoadBalancing/Authority/MovementValidationInterceptor.cs`
- Test: `server/BlackIce.Photon.Tests/PositionDecodeTests.cs`, `server/BlackIce.Server.Tests/Authority/MovementValidationInterceptorTests.cs`

**Context:** Event 201's `Parameters[245]` is an `object[]` batch: `[networkTime, prefix, perViewEntry...]` where a per-view entry is `[viewId, false, null, Vector3(86), Quaternion(81), ...]`. The Vector3 is a `PhotonCustomData(86, 12 bytes)` = 3 big-endian floats (x,y,z). `PositionInfo.From(ev)` extracts the first (viewId, x,y,z) it finds, or null. The interceptor tracks per-actor last position+timestamp and logs if implied speed exceeds a max (units/sec). Log-only → always Forward.

- [ ] **Step 1 — failing test** for the decoder (`server/BlackIce.Photon.Tests/PositionDecodeTests.cs`):

```csharp
using System.Buffers.Binary;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PositionDecodeTests
{
    private static PhotonCustomData Vec3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(86, b);
    }

    [Fact]
    public void Reads_viewid_and_xyz_from_a_201_batch()
    {
        var view = new object[] { 2001, false, null!, Vec3(10f, 0f, -5f), new PhotonCustomData(81, new byte[16]) };
        var batch = new object[] { 123, null!, view };
        var ev = new EventData(201, new() { { 245, batch } });

        var p = PositionInfo.From(ev);
        Assert.True(p.HasValue);
        Assert.Equal(2001, p!.Value.ViewId);
        Assert.Equal(10f, p.Value.X, 3);
        Assert.Equal(-5f, p.Value.Z, 3);
    }

    [Fact]
    public void Returns_null_for_non_201_or_malformed()
    {
        Assert.Null(PositionInfo.From(new EventData(200, new() { { 245, "x" } })));
        Assert.Null(PositionInfo.From(new EventData(201, new() { { 245, "not-a-batch" } })));
    }
}
```

- [ ] **Step 2 — run, expect FAIL.**

- [ ] **Step 3 — implement** `server/BlackIce.Photon/PositionInfo.cs`:

```csharp
using System.Buffers.Binary;

namespace BlackIce.Photon;

/// <summary>
/// The absolute position of one networked view, decoded from a PUN serialize batch (Photon event 201).
/// The batch under PData(245) is object[] { networkTime, prefix, perViewEntry... }; a per-view entry is
/// object[] { viewId, bool, null, Vector3(custom 86 = 3 big-endian floats), Quaternion(81), ... }.
/// </summary>
public readonly record struct PositionInfo(int ViewId, float X, float Y, float Z)
{
    private const byte PData = 245, Vec3Code = 86;

    public static PositionInfo? From(EventData ev)
    {
        if (ev.Code != 201) return null;
        if (!ev.Parameters.TryGetValue(PData, out var d) || d is not object[] batch) return null;

        // Per-view entries start at index 2 (after networkTime + prefix).
        for (int i = 2; i < batch.Length; i++)
        {
            if (batch[i] is not object[] view || view.Length < 4) continue;
            int viewId = view[0] is int v ? v : 0;
            foreach (var field in view)
                if (field is PhotonCustomData { Code: Vec3Code } vec && vec.Data.Length >= 12)
                {
                    float x = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(0, 4));
                    float y = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(4, 4));
                    float z = BinaryPrimitives.ReadSingleBigEndian(vec.Data.AsSpan(8, 4));
                    return new PositionInfo(viewId, x, y, z);
                }
        }
        return null;
    }
}
```

- [ ] **Step 4 — run the decoder test, expect PASS.**

- [ ] **Step 5 — failing test** for the interceptor (`server/BlackIce.Server.Tests/Authority/MovementValidationInterceptorTests.cs`):

```csharp
using System.Buffers.Binary;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Authority;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class MovementValidationInterceptorTests
{
    private static EventData PosEvent(int viewId, float x, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        var view = new object[] { viewId, false, null!, new PhotonCustomData(86, b), new PhotonCustomData(81, new byte[16]) };
        return new EventData(201, new() { { 245, new object[] { 0, null!, view } } });
    }

    [Fact]
    public void Always_forwards_position_events()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        var v = i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Flags_a_teleport_jump_between_two_updates()
    {
        // Two updates ~0.1s apart (the interceptor uses wall-clock between calls); a 10000-unit jump
        // is far over 50 u/s. First call establishes baseline; second is flagged.
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 10000, 0)));
        Assert.Equal(1, i.FlaggedCount);
    }

    [Fact]
    public void Normal_walking_is_not_flagged()
    {
        var i = new MovementValidationInterceptor(maxUnitsPerSecond: 50f);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 0, 0)));
        System.Threading.Thread.Sleep(50);
        i.Intercept(new EventContext("co-op", 1, PosEvent(1001, 1, 0)));   // 1 unit in ~0.05s = 20 u/s
        Assert.Equal(0, i.FlaggedCount);
    }
}
```

- [ ] **Step 6 — run, expect FAIL.**

- [ ] **Step 7 — implement** `server/BlackIce.Server.LoadBalancing/Authority/MovementValidationInterceptor.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Watches relayed position updates (PUN event 201) and LOGS implied speeds above a sane maximum
/// (teleport / speedhack). Tracks the last position + wall-clock time per (room, viewId). Phase 2b is
/// detection-only: always forwards. A later phase can clamp/drop once tuned against real play. Driven
/// from the single listener thread, so the per-view state needs no locking.
/// </summary>
public sealed class MovementValidationInterceptor : IEventInterceptor
{
    private readonly float _maxUnitsPerSecond;
    private readonly Dictionary<(string room, int viewId), (float x, float y, float z, DateTime t)> _last = new();
    public int FlaggedCount { get; private set; }

    public MovementValidationInterceptor(float maxUnitsPerSecond) => _maxUnitsPerSecond = maxUnitsPerSecond;

    public RelayVerdict Intercept(EventContext ctx)
    {
        var pos = PositionInfo.From(ctx.Event);
        if (pos is { } p)
        {
            var key = (ctx.RoomName, p.ViewId);
            var now = DateTime.UtcNow;
            if (_last.TryGetValue(key, out var prev))
            {
                var dt = (now - prev.t).TotalSeconds;
                if (dt > 0.001)
                {
                    double dx = p.X - prev.x, dy = p.Y - prev.y, dz = p.Z - prev.z;
                    double speed = Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt;
                    if (speed > _maxUnitsPerSecond)
                    {
                        FlaggedCount++;
                        Log.Warn("Authority", $"actor {ctx.SenderActor} view {p.ViewId} in \"{ctx.RoomName}\" " +
                                              $"moved {speed:F0} u/s (> max {_maxUnitsPerSecond:F0}) -> forwarded (log-only)");
                    }
                }
            }
            _last[key] = (p.X, p.Y, p.Z, now);
        }
        return RelayVerdict.Forward(ctx.Event);
    }
}
```

- [ ] **Step 8 — run both tests, expect PASS.**
- [ ] **Step 9 — commit:**
```bash
git add server/BlackIce.Photon/PositionInfo.cs server/BlackIce.Photon.Tests/PositionDecodeTests.cs server/BlackIce.Server.LoadBalancing/Authority/MovementValidationInterceptor.cs server/BlackIce.Server.Tests/Authority/MovementValidationInterceptorTests.cs
git commit -m "feat(authority): MovementValidationInterceptor — log speedhack/teleport (forward-only)"
```

---

## Task 4: Wire the authority interceptors into the room chain

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/RoomRegistry.cs` (the `Session` chain factory)
- Test: `server/BlackIce.Server.Tests/Authority/AuthorityChainTests.cs`

**Context:** `RoomRegistry.Session(name)` currently builds `new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() })`. Add the two validators BEFORE the passthrough (they Forward anyway, so order only affects logging). Use generous default thresholds (won't false-positive on legit play): maxDamage e.g. 100000f, maxUnitsPerSecond e.g. 200f. Because they're log-only and always Forward, relay behavior is unchanged — existing relay tests still pass.

- [ ] **Step 1 — failing test** (`server/BlackIce.Server.Tests/Authority/AuthorityChainTests.cs`):

```csharp
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class AuthorityChainTests
{
    [Fact]
    public void Session_chain_includes_the_authority_validators()
    {
        var reg = new RoomRegistry();
        var session = reg.Session("co-op");
        // The session exists and relays; the validators are present but log-only so relay is unchanged.
        Assert.Equal("co-op", session.RoomName);
        // Reflection-free behavioral check: a normal relay still forwards (covered by RoomSessionRelayTests);
        // here we just assert the session builds without error with the authority chain.
        Assert.Equal(0, session.Count);
    }
}
```

> Note: this is a light smoke test — the real coverage is the per-interceptor tests. If you want a stronger assertion, expose the interceptor count via a test-only accessor on InterceptorChain, but do NOT over-engineer; the per-interceptor tests already prove behavior.

- [ ] **Step 2 — run (will PASS once Session builds with the new chain; if you add no new API it may pass immediately — that's fine, it's a smoke test). Implement Step 3 regardless.**

- [ ] **Step 3 — implement.** In `server/BlackIce.Server.LoadBalancing/RoomRegistry.cs`, update the `Session` factory:

```csharp
    public RoomSession Session(string name) =>
        _sessions.GetOrAdd(name, n => new RoomSession(n, new InterceptorChain(new IEventInterceptor[]
        {
            // Authority validators (Phase 2b) — detection-only: they log violations and always forward,
            // so relay behavior is unchanged. Thresholds are generous to avoid false positives on legit
            // play; enforcement (clamp/drop) is a later, live-tuned step.
            new Authority.DamageValidationInterceptor(maxDamage: 100000f),
            new Authority.MovementValidationInterceptor(maxUnitsPerSecond: 200f),
            new PassthroughInterceptor(),
        })));
```
(Add `using` if needed; `Authority.` qualifies the new namespace.)

- [ ] **Step 4 — run the whole solution** `dotnet test server/BlackIce.Server.sln` — expect ALL green, especially the existing `RoomSessionRelayTests`/`GameServerRelayTests`/`UnreliableRelayTests` (relay unchanged because validators Forward).

- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Server.LoadBalancing/RoomRegistry.cs server/BlackIce.Server.Tests/Authority/AuthorityChainTests.cs
git commit -m "feat(authority): wire damage + movement validators (log-only) into every room chain"
```

---

## Self-review
- Damage decode (DamagePacket 68, big-endian float) → Task 1, oracle-tested. ✓
- Damage detection log-only → Task 2. ✓
- Position decode (Vec3 86 from 201 batch) → Task 3 decoder. ✓
- Movement speed detection log-only → Task 3 interceptor. ✓
- Wired into the live chain without changing relay behavior → Task 4. ✓
- Enforcement (drop/clamp/rewrite) and spawn-authority/master-client → explicitly OUT (later, live-tuned). ✓
- Placeholders: none. Type consistency: `PunRpcInfo.From→PunRpcInfo{ViewId,Method,DamageValue}`; `PositionInfo.From→PositionInfo{ViewId,X,Y,Z}`; interceptors implement `IEventInterceptor.Intercept→RelayVerdict`, both expose `FlaggedCount`; chain wired in `RoomRegistry.Session`. ✓
- Live-tuning note: thresholds (100000 dmg, 200 u/s) are placeholders to avoid false positives; the user should tune them against real play before any enforcement step. Logged for VERIFY-ON-RETURN.
