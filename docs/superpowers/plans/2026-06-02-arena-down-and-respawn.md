# Real Arena Down-and-Respawn Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `killfeed`/`arena` plugins' HP-summing kill approximation with detection of true player deaths (from the captured `KilledPlayerRemote` RPC), death-based team scoring, and server-orchestrated respawns at round reset (`TeleportImmediately`+`BecomeTangible`).

**Architecture:** A new `RpcShortcuts` table lets `PunRpcInfo` resolve shortcut-indexed RPCs by name, so the relay's `killfeed` interceptor can recognize `KilledPlayerRemote`, publish a `DeathNotice` on the existing `KillBus`, and the `arena` plugin scores the victim's opposing team. On round reset the `arena` sends server-authored respawn RPCs (built by `ServerRpc`, reusing a promoted `PhotonCustomData.Vector3` factory) to every room participant. All server-side, Steam-free, no game DLL needed for tests.

**Tech Stack:** C# / .NET 8, xUnit. Projects: `BlackIce.Photon` (wire types), `BlackIce.Server.LoadBalancing` (relay/plugins), `BlackIce.Server.Core` (options). Tests in `BlackIce.Photon.Tests` and `BlackIce.Server.Tests` (the latter has `InternalsVisibleTo`, so internal types are directly testable).

**Spec:** `docs/superpowers/specs/2026-06-02-arena-down-and-respawn-design.md`

**Conventions:** Conventional commits, scoped (`feat(lb):`, `feat(data):`, `refactor(...):`, `test(...):`).
Build with `dotnet build server/BlackIce.Server.sln`; test with `dotnet test server/BlackIce.Server.sln`. **Note:** if the dev server is running it locks `BlackIce.Photon.dll`; stop it (or test only the relevant project) before a full solution build.

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `server/BlackIce.Photon/PhotonCustomData.cs` | modify | add static `Vector3(x,y,z)` factory (the one Vector3 encoder) |
| `server/BlackIce.Photon/RpcShortcuts.cs` | create | ordered shortcut-index → method-name table (88 entries) |
| `server/BlackIce.Photon/PunRpcInfo.cs` | modify | resolve shortcut RPCs to a method name; expose `MethodIndex` + `Args` |
| `server/BlackIce.Server.LoadBalancing/KillBus.cs` | modify | add `DeathNotice` record + `Died` event + `PublishDeath` |
| `server/BlackIce.Server.LoadBalancing/ServerRpc.cs` | modify | add `Teleport` + `BecomeTangible` builders |
| `server/BlackIce.Server.Core/ArenaOptions.cs` | modify | add `RespawnAtReset` + spawn-point fields |
| `server/BlackIce.Server.LoadBalancing/Plugins/KillfeedPlugin.cs` | modify | real-death detector + announcer; retire HP-summing |
| `server/BlackIce.Server.LoadBalancing/Plugins/ArenaPlugin.cs` | modify | death-based scoring; respawn at reset |
| `server/BlackIce.Server.LoadBalancing/Bots/GameActions.cs` | modify | `Vec3` delegates to the shared factory (DRY) |
| `server/BlackIce.Photon.Tests/RpcShortcutsTests.cs` | create | table lookups |
| `server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs` | modify | update the shortcut test (now resolves) |
| `server/BlackIce.Photon.Tests/PhotonCustomDataTests.cs` | create | Vector3 factory encoding |
| `server/BlackIce.Server.Tests/ServerRpcTests.cs` | create | respawn RPC builders |
| `server/BlackIce.Server.Tests/KillBusTests.cs` | create | death channel |
| `server/BlackIce.Server.Tests/KillfeedDeathTests.cs` | create | death detection/debounce |
| `server/BlackIce.Server.Tests/ArenaMatchTests.cs` | create | scoring + respawn |

---

### Task 1: Promote the Vector3 encoder to a shared factory

**Files:**
- Modify: `server/BlackIce.Photon/PhotonCustomData.cs`
- Modify: `server/BlackIce.Server.LoadBalancing/Bots/GameActions.cs`
- Test: `server/BlackIce.Photon.Tests/PhotonCustomDataTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Photon.Tests/PhotonCustomDataTests.cs`:

```csharp
using System;
using System.Buffers.Binary;
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class PhotonCustomDataTests
{
    [Fact]
    public void Vector3_factory_encodes_three_big_endian_floats_with_the_vector3_code()
    {
        var v = PhotonCustomData.Vector3(520f, 3f, 469.5f);
        Assert.Equal(PhotonCodes.CustomType.Vector3, v.Code);
        Assert.Equal(12, v.Data.Length);
        Assert.Equal(520f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(0)), 3);
        Assert.Equal(3f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(4)), 3);
        Assert.Equal(469.5f, BinaryPrimitives.ReadSingleBigEndian(v.Data.AsSpan(8)), 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter Vector3_factory_encodes_three_big_endian_floats_with_the_vector3_code`
Expected: FAIL — `PhotonCustomData` has no `Vector3` member (compile error).

- [ ] **Step 3: Add the factory**

Replace the body of `server/BlackIce.Photon/PhotonCustomData.cs` with:

```csharp
using System;
using System.Buffers.Binary;

namespace BlackIce.Photon;

/// <summary>
/// A registered Photon custom type as it appears on the wire: a 1-byte type code plus its raw
/// serialized bytes (e.g. PUN's Vector3 = code 86, three big-endian floats). Phase 1 preserves
/// these verbatim so the stream stays aligned; decoding specific custom types is a later concern.
/// </summary>
public sealed record PhotonCustomData(byte Code, byte[] Data)
{
    /// <summary>Builds a PUN Vector3 (custom type 86): three big-endian floats. The single Vector3
    /// encoder shared by server-authored RPCs (respawn teleports) and the soak bots.</summary>
    public static PhotonCustomData Vector3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }
}
```

- [ ] **Step 4: Point `GameActions.Vec3` at the shared factory**

In `server/BlackIce.Server.LoadBalancing/Bots/GameActions.cs`, replace the `Vec3` helper body:

```csharp
    private static PhotonCustomData Vec3(float x, float y, float z) => PhotonCustomData.Vector3(x, y, z);
```

(Leave the call sites calling `Vec3(...)`; they now route through the one factory.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter Vector3_factory`
Expected: PASS.
Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter GameActions`
Expected: PASS (existing `GameActionsTests` still green — the bot Vector3 path is unchanged in behavior).

- [ ] **Step 6: Commit**

```bash
git add server/BlackIce.Photon/PhotonCustomData.cs server/BlackIce.Server.LoadBalancing/Bots/GameActions.cs server/BlackIce.Photon.Tests/PhotonCustomDataTests.cs
git commit -m "refactor(photon): single Vector3 custom-type factory, reused by bots"
```

---

### Task 2: RpcShortcuts table

**Files:**
- Create: `server/BlackIce.Photon/RpcShortcuts.cs`
- Test: `server/BlackIce.Photon.Tests/RpcShortcutsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Photon.Tests/RpcShortcutsTests.cs`:

```csharp
using BlackIce.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class RpcShortcutsTests
{
    [Fact]
    public void Resolves_known_indices_to_method_names()
    {
        Assert.Equal("KilledPlayerRemote", RpcShortcuts.Name(32));
        Assert.Equal("ReceiveChatMessage", RpcShortcuts.Name(39));
        Assert.Equal("TeleportImmediately", RpcShortcuts.Name(66));
        Assert.Equal("BecomeTangible", RpcShortcuts.Name(9));
    }

    [Fact]
    public void Has_the_full_captured_table_and_rejects_out_of_range()
    {
        Assert.Equal(88, RpcShortcuts.Methods.Count);
        Assert.Null(RpcShortcuts.Name(-1));
        Assert.Null(RpcShortcuts.Name(88));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter RpcShortcuts`
Expected: FAIL — `RpcShortcuts` does not exist (compile error).

- [ ] **Step 3: Create the table**

Create `server/BlackIce.Photon/RpcShortcuts.cs`. The list order is authoritative (index = position) and mirrors `docs/protocol/generated/rpc-shortcuts.csv`:

```csharp
using System.Collections.Generic;

namespace BlackIce.Photon;

/// <summary>
/// PUN's ordered RPC method list (the project's <c>RpcList</c>), captured live from the game client.
/// A client may send a frequently-used RPC as a byte <b>index</b> into this list (Photon RPC key 5,
/// the "method shortcut") instead of by name — e.g. <c>KilledPlayerRemote</c> arrives as index 32.
/// This table resolves that index back to a method name so the relay can recognize the call.
/// Mirrors <c>docs/protocol/generated/rpc-shortcuts.csv</c>; regenerate both from a fresh
/// <c>BlackIce.OpLogger</c> "rpclist" capture after a game update (indices are not version-stable).
/// </summary>
public static class RpcShortcuts
{
    public static readonly IReadOnlyList<string> Methods = new[]
    {
        "AddAggro", "AddBuffRPC", "AddImpact", "AddImpactNetwork", "AddRAMNetwork", "AddSpawnedEnemies",
        "AddXP", "AddXPRPC", "BarrierSetup", "BecomeTangible", "CancelHackSecondary", "ChangeColor",
        "ClickRpc", "Cloak", "DestroyRpc", "Die", "DieByViewID", "DropLoot", "DropLootForLocalPlayer",
        "EggHatchEarly", "EndHack", "ExplodeObjects", "ExplosionParticlesNetwork", "FinishEndingHack",
        "GetColorCallback", "GetLock", "GhostIntangible", "GoIntangible", "GrapplingHookOther",
        "InitializeFromNetwork", "KickRemote", "KilledPlayer", "KilledPlayerRemote", "KillEnemyPhase",
        "KillProjectileOther", "LoadRemoteModIcon", "LockGranted", "NotifyMine", "ParentToThisNetwork",
        "ReceiveChatMessage", "RefreshModel", "RequestModIcon", "RequestParent", "ResetAtNetworkTime",
        "ReturnActual", "SetActiveAtNetworkTime", "SetColor", "SetDamageTaken", "SetDying", "SetHealth",
        "SetItemRPC", "SetLinkedPrimaryPawn", "SetMaxShield", "SetShieldValues", "SetupHack",
        "SetupLinkRequest", "SetupMineNetwork", "SetupRequest", "SetupXP", "Shatter", "SpawnDisc",
        "SpawnProjectile", "SyncUnhackServerRPC", "TakeDamage", "TakeDamageOwner", "Teleport",
        "TeleportImmediately", "TriggerGrenade", "Uncloak", "Unlock", "UpdateDifficultyRPC",
        "UpdateHighestServerHackedRPC", "UpdatePVPRPC", "WakeEnemyAfterDelay", "SpawnProjectileLocal",
        "SpawnProjectileRemote", "AddTempHP", "KilledPlayerBuildingSecondary", "NonexplosionParticlesNetwork",
        "SetHostWorldState", "SetWorldStateFlag", "ShareHostWorldStateMaster", "SetCreditsRPC",
        "NotifySeen", "RemoveDebuffsRPC", "DiscoGetLock", "DiscoLocked", "DiscoUnlockPawn",
    };

    /// <summary>The method name at a shortcut index, or null if the index is out of range.</summary>
    public static string? Name(int index) => index >= 0 && index < Methods.Count ? Methods[index] : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter RpcShortcuts`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Photon/RpcShortcuts.cs server/BlackIce.Photon.Tests/RpcShortcutsTests.cs
git commit -m "feat(photon): RpcShortcuts table to resolve shortcut-indexed RPCs"
```

---

### Task 3: PunRpcInfo resolves shortcut RPCs + exposes Args

**Files:**
- Modify: `server/BlackIce.Photon/PunRpcInfo.cs`
- Test: `server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs` (update the existing shortcut test, add new ones)

- [ ] **Step 1: Update the existing shortcut test and add resolution/args tests**

In `server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs`, **replace** the `Handles_shortcut_rpc_with_null_method_name` test with:

```csharp
    [Fact]
    public void Resolves_shortcut_rpc_to_a_method_name_and_index()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1001 },
                    { (byte)5, (byte)73 },                      // shortcut index 73 = WakeEnemyAfterDelay
                    { (byte)4, new object[] { DamagePacket(10f) } },
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Equal("WakeEnemyAfterDelay", info!.Value.Method);   // now resolved via RpcShortcuts
        Assert.Equal(73, info.Value.MethodIndex);
        Assert.Equal(10f, info.Value.DamageValue!.Value, 3);
    }

    [Fact]
    public void Exposes_rpc_args_for_inspection()
    {
        var ev = new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)5, (byte)32 },                      // KilledPlayerRemote
                    { (byte)4, new object[] { 6001 } },         // victim pawn viewId
                } },
        });
        var info = PunRpcInfo.From(ev);
        Assert.True(info.HasValue);
        Assert.Equal("KilledPlayerRemote", info!.Value.Method);
        Assert.NotNull(info.Value.Args);
        Assert.Equal(6001, Assert.IsType<int>(info.Value.Args![0]));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter PunRpc`
Expected: FAIL — `PunRpcInfo` has no `MethodIndex`/`Args` and does not resolve shortcuts (compile errors / assertion failures).

- [ ] **Step 3: Extend PunRpcInfo**

Replace `server/BlackIce.Photon/PunRpcInfo.cs` with:

```csharp
using System.Buffers.Binary;
using System.Collections;

namespace BlackIce.Photon;

/// <summary>
/// Decoded view of a PUN RPC event (Photon event code 200) for authority checks: the target view id,
/// the method name (resolved from the shortcut index via <see cref="RpcShortcuts"/> when sent that way),
/// the raw shortcut index, the argument array, and — if any argument is a DamagePacket custom type
/// (code 68) — its damage value (first 4 bytes, big-endian float) plus the raw packet bytes.
/// Pure read over an already-decoded EventData; no transport parsing.
/// </summary>
public readonly record struct PunRpcInfo(
    int ViewId, string? Method, float? DamageValue, byte[]? DamagePacket, int? MethodIndex = null, object[]? Args = null)
{
    /// <summary>
    /// True when the masked byte at <paramref name="offset"/> in the DamagePacket is non-zero — i.e.
    /// <c>(packet[offset] &amp; mask) != 0</c>. The caller supplies the game-specific offset and mask so a
    /// single flag bit can be isolated from others sharing the byte (e.g. WeakPoint vs Crit).
    /// </summary>
    public bool IsHeadshot(int offset, byte mask = 0xFF) =>
        DamagePacket is { } p && offset >= 0 && offset < p.Length && (p[offset] & mask) != 0;

    /// <summary>Decodes <paramref name="ev"/> as a PUN RPC, or null if it is not event 200 with an RPC table.</summary>
    public static PunRpcInfo? From(EventData ev)
    {
        if (ev.Code != PhotonCodes.PunEvent.Rpc) return null;
        if (!ev.Parameters.TryGetValue(PhotonCodes.Param.Data, out var d) || d is not IDictionary rpc) return null;

        int viewId = rpc.Contains(PhotonCodes.RpcKey.ViewId) && rpc[PhotonCodes.RpcKey.ViewId] is int v ? v : 0;
        string? method = rpc.Contains(PhotonCodes.RpcKey.MethodName) ? rpc[PhotonCodes.RpcKey.MethodName] as string : null;

        int? shortcut = null;
        if (rpc.Contains(PhotonCodes.RpcKey.MethodShortcut))
            shortcut = rpc[PhotonCodes.RpcKey.MethodShortcut] switch { byte b => b, int i => i, _ => (int?)null };
        if (method is null && shortcut is int idx) method = RpcShortcuts.Name(idx);

        object[]? args = rpc.Contains(PhotonCodes.RpcKey.Args) ? rpc[PhotonCodes.RpcKey.Args] as object[] : null;

        float? damage = null;
        byte[]? packet = null;
        if (args is not null)
            foreach (var a in args)
                if (a is PhotonCustomData { Code: PhotonCodes.CustomType.DamagePacket } dp && dp.Data.Length >= 4)
                {
                    damage = BinaryPrimitives.ReadSingleBigEndian(dp.Data.AsSpan(0, 4));
                    packet = dp.Data;
                    break;
                }
        return new PunRpcInfo(viewId, method, damage, packet, shortcut, args);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj`
Expected: PASS (all PunRpc tests, including the updated shortcut test; the named-RPC, non-RPC, and no-damage tests still pass).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Photon/PunRpcInfo.cs server/BlackIce.Photon.Tests/PunRpcDecodeTests.cs
git commit -m "feat(photon): PunRpcInfo resolves shortcut RPCs and exposes args"
```

---

### Task 4: KillBus death channel

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/KillBus.cs`
- Test: `server/BlackIce.Server.Tests/KillBusTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Server.Tests/KillBusTests.cs`:

```csharp
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class KillBusTests
{
    [Fact]
    public void PublishDeath_invokes_the_Died_subscribers_with_the_notice()
    {
        var bus = new KillBus();
        DeathNotice? seen = null;
        bus.Died += n => seen = n;

        bus.PublishDeath(new DeathNotice("co-op", 6));

        Assert.NotNull(seen);
        Assert.Equal("co-op", seen!.Value.Room);
        Assert.Equal(6, seen.Value.Victim);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter KillBus`
Expected: FAIL — no `DeathNotice` / `Died` / `PublishDeath` (compile error).

- [ ] **Step 3: Add the death channel**

In `server/BlackIce.Server.LoadBalancing/KillBus.cs`, add the record next to `KillNotice`:

```csharp
/// <summary>A server-detected real death: the room and the victim actor. No killer — the death RPC
/// (KilledPlayerRemote) carries only the victim. Kill credit is a future, separately-captured concern.</summary>
public readonly record struct DeathNotice(string Room, int Victim);
```

And inside the `KillBus` class, add alongside `Killed`/`Publish`:

```csharp
    /// <summary>Raised when the relay detects a real player death. Handlers run on the Game listener thread.</summary>
    public event Action<DeathNotice>? Died;

    public void PublishDeath(DeathNotice notice) => Died?.Invoke(notice);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter KillBus`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/KillBus.cs server/BlackIce.Server.Tests/KillBusTests.cs
git commit -m "feat(lb): KillBus death channel (DeathNotice/Died)"
```

---

### Task 5: ServerRpc respawn builders

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/ServerRpc.cs`
- Test: `server/BlackIce.Server.Tests/ServerRpcTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Server.Tests/ServerRpcTests.cs`:

```csharp
using System.Buffers.Binary;
using System.Collections;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class ServerRpcTests
{
    private static IDictionary Rpc(EventData ev) =>
        (IDictionary)ev.Parameters[PhotonCodes.Param.Data];

    [Fact]
    public void Teleport_targets_the_actor_pawn_with_a_vector3_arg()
    {
        var ev = ServerRpc.Teleport(actor: 6, 520f, 3f, 469.5f);
        Assert.Equal(PhotonCodes.PunEvent.Rpc, ev.Code);

        var rpc = Rpc(ev);
        Assert.Equal(6 * 1000 + 1, rpc[PhotonCodes.RpcKey.ViewId]);            // pawn viewId
        Assert.Equal("TeleportImmediately", rpc[PhotonCodes.RpcKey.MethodName]);

        var args = (object[])rpc[PhotonCodes.RpcKey.Args]!;
        var pos = Assert.IsType<PhotonCustomData>(args[0]);
        Assert.Equal(PhotonCodes.CustomType.Vector3, pos.Code);
        Assert.Equal(520f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(0)), 3);
        Assert.Equal(3f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(4)), 3);
        Assert.Equal(469.5f, BinaryPrimitives.ReadSingleBigEndian(pos.Data.AsSpan(8)), 3);
    }

    [Fact]
    public void BecomeTangible_targets_the_actor_pawn_with_no_args()
    {
        var ev = ServerRpc.BecomeTangible(actor: 6);
        var rpc = Rpc(ev);
        Assert.Equal(6 * 1000 + 1, rpc[PhotonCodes.RpcKey.ViewId]);
        Assert.Equal("BecomeTangible", rpc[PhotonCodes.RpcKey.MethodName]);
        Assert.Empty((object[])rpc[PhotonCodes.RpcKey.Args]!);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter ServerRpc`
Expected: FAIL — `ServerRpc` has no `Teleport`/`BecomeTangible` (compile error).

- [ ] **Step 3: Add the builders**

In `server/BlackIce.Server.LoadBalancing/ServerRpc.cs`, add these methods inside the `ServerRpc` class (after `Chat`). They mirror `Chat`'s shape (RPC on the actor's pawn view, addressed by method name):

```csharp
    /// <summary>A <c>TeleportImmediately(Vector3)</c> RPC on <paramref name="actor"/>'s pawn view — used to
    /// respawn a participant to a spawn point at round reset (captured respawn sequence step 1 of 2).</summary>
    public static EventData Teleport(int actor, float x, float y, float z) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "TeleportImmediately" },
                    { PhotonCodes.RpcKey.Args, new object[] { PhotonCustomData.Vector3(x, y, z) } },
                } },
        });

    /// <summary>A <c>BecomeTangible()</c> RPC on <paramref name="actor"/>'s pawn view — the second half of
    /// the captured respawn sequence (re-enables collision / "alive").</summary>
    public static EventData BecomeTangible(int actor) =>
        new(PhotonCodes.PunEvent.Rpc, new Dictionary<byte, object>
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, actor * MaxViewIdsPerActor + AvatarViewSlot },
                    { PhotonCodes.RpcKey.MethodName, "BecomeTangible" },
                    { PhotonCodes.RpcKey.Args, System.Array.Empty<object>() },
                } },
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter ServerRpc`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/ServerRpc.cs server/BlackIce.Server.Tests/ServerRpcTests.cs
git commit -m "feat(lb): ServerRpc Teleport/BecomeTangible respawn builders"
```

---

### Task 6: Killfeed becomes a real-death detector

This **replaces** the HP-summing model. The new interceptor watches for `KilledPlayerRemote`, derives the
victim actor from the RPC's first arg (the pawn viewId), debounces repeats, publishes a `DeathNotice`, and
announces the elimination. State shrinks to an on/off flag plus a per-room "currently dead" set.

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/Plugins/KillfeedPlugin.cs` (full rewrite)
- Test: `server/BlackIce.Server.Tests/KillfeedDeathTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Server.Tests/KillfeedDeathTests.cs`:

```csharp
using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests;

public class KillfeedDeathTests
{
    // A KilledPlayerRemote RPC sent by shortcut index 32, victim = pawn viewId of `victimActor`.
    private static EventData Death(int victimActor) => new(200, new()
    {
        { 245, new Dictionary<object, object>
            {
                { (byte)5, (byte)32 },                                  // KilledPlayerRemote
                { (byte)4, new object[] { victimActor * 1000 + 1 } },   // victim pawn viewId
            } },
    });

    private static (KillfeedInterceptor i, KillfeedState s, KillBus bus) Make()
    {
        var s = new KillfeedState { On = true };
        var bus = new KillBus();
        return (new KillfeedInterceptor(s, bus), s, bus);
    }

    [Fact]
    public void A_death_rpc_publishes_a_DeathNotice_and_announces()
    {
        var (i, _, bus) = Make();
        DeathNotice? seen = null;
        bus.Died += n => seen = n;

        var v = i.Intercept(new EventContext("co-op", 1, Death(victimActor: 6)));

        Assert.Equal(RelayAction.Originate, v.Action);     // original death RPC + an announcement
        Assert.Single(v.Originated);
        Assert.NotNull(seen);
        Assert.Equal(6, seen!.Value.Victim);
    }

    [Fact]
    public void A_repeat_death_for_an_already_dead_victim_is_debounced()
    {
        var (i, _, bus) = Make();
        int notices = 0;
        bus.Died += _ => notices++;

        i.Intercept(new EventContext("co-op", 1, Death(6)));
        i.Intercept(new EventContext("co-op", 1, Death(6)));   // repeat before any reset

        Assert.Equal(1, notices);
    }

    [Fact]
    public void Non_death_rpcs_are_forwarded_untouched()
    {
        var (i, _, _) = Make();
        var v = i.Intercept(new EventContext("co-op", 1, new EventData(201, new() { { 245, "pos" } })));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Off_by_default_forwards_everything()
    {
        var s = new KillfeedState();                 // On == false
        var i = new KillfeedInterceptor(s, new KillBus());
        Assert.Equal(RelayAction.Forward, i.Intercept(new EventContext("co-op", 1, Death(6))).Action);
    }

    [Fact]
    public void A_room_reset_clears_the_dead_set_so_the_victim_can_die_again()
    {
        var (i, s, bus) = Make();
        int notices = 0;
        bus.Died += _ => notices++;

        i.Intercept(new EventContext("co-op", 1, Death(6)));
        s.ClearDead("co-op");                          // round reset
        i.Intercept(new EventContext("co-op", 1, Death(6)));

        Assert.Equal(2, notices);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter KillfeedDeath`
Expected: FAIL — the new `KillfeedInterceptor(state, bus)` ctor, `KillfeedState.ClearDead`, etc. don't exist yet (compile errors).

- [ ] **Step 3: Rewrite the plugin**

Replace the entire contents of `server/BlackIce.Server.LoadBalancing/Plugins/KillfeedPlugin.cs` with:

```csharp
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin that detects <b>real player deaths</b> from the relay and announces them, with zero
/// client support. The game broadcasts a player's death as a <c>KilledPlayerRemote</c> RPC carrying the
/// victim's pawn viewId (captured live — see <c>docs/protocol/03-rpc-catalog.md</c>); this plugin watches
/// for it, announces the elimination over vanilla chat, and publishes a <see cref="DeathNotice"/> on the
/// <see cref="KillBus"/> so the <c>arena</c> match plugin can score it. (It replaces an earlier model that
/// summed <c>TakeDamage</c> toward an assumed max-HP — which never fired, since damage is resolved
/// master-side and no <c>TakeDamage</c> transits the wire.) Off by default; an admin runs <c>killfeed on</c>.
/// </summary>
public sealed class KillfeedPlugin : IServerPlugin
{
    public string Name => "killfeed";
    public string Description => "Real-death elimination feed: detects KilledPlayerRemote, announces it via vanilla chat, and publishes deaths for the arena scorer. Off by default.";
    public int Order => 100;   // react AFTER the validators

    public void Configure(PluginBuilder builder)
    {
        var state = new KillfeedState();
        var bus = (KillBus?)builder.Services.GetService(typeof(KillBus));

        if (bus is not null) bus.RoomReset += state.ClearDead;   // a new round lets everyone die again

        builder
            .AddInterceptor(() => new KillfeedInterceptor(state, bus))
            .OnActorLeft(ctx => state.Forget(ctx.RoomName, ctx.Actor))
            .AddCommands(new KillfeedCommands(state));
    }
}

/// <summary>Kill-feed on/off plus the per-room "currently dead" set used to debounce repeated death RPCs
/// (the game may resend). Accessed on the Game listener thread with the flag also written from the console
/// thread; a concurrent map and an atomic bool cover that.</summary>
internal sealed class KillfeedState
{
    public bool On;

    private readonly ConcurrentDictionary<(string Room, int Actor), bool> _dead = new();

    /// <summary>Marks a victim dead; returns true only the first time (so the caller scores once per death).</summary>
    public bool MarkDead(string room, int actor) => _dead.TryAdd((room, actor), true);

    /// <summary>Drops a departed player's dead-flag (called from the leave hook).</summary>
    public void Forget(string room, int actor) => _dead.TryRemove((room, actor), out _);

    /// <summary>Clears every dead-flag for a room (called on a round/match reset).</summary>
    public void ClearDead(string room)
    {
        foreach (var key in _dead.Keys.Where(k => k.Room == room).ToArray()) _dead.TryRemove(key, out _);
    }
}

/// <summary>
/// Per-relay death detector: when a <c>KilledPlayerRemote</c> RPC passes through, derives the victim actor
/// from the pawn viewId in the first arg, debounces, publishes a <see cref="DeathNotice"/>, and announces
/// the elimination (forwarded alongside the original RPC so clients still process the death).
/// </summary>
internal sealed class KillfeedInterceptor : IEventInterceptor
{
    private const int MaxViewIdsPerActor = 1000;   // viewId / 1000 = owning actor
    private readonly KillfeedState _state;
    private readonly KillBus? _bus;

    public KillfeedInterceptor(KillfeedState state, KillBus? bus)
    {
        _state = state;
        _bus = bus;
    }

    public RelayVerdict Intercept(EventContext ctx)
    {
        if (!_state.On) return RelayVerdict.Forward(ctx.Event);

        var info = PunRpcInfo.From(ctx.Event);
        if (info is not { Method: "KilledPlayerRemote" } rpc) return RelayVerdict.Forward(ctx.Event);
        if (rpc.Args is not { Length: > 0 } || rpc.Args[0] is not int victimView) return RelayVerdict.Forward(ctx.Event);

        int victim = victimView / MaxViewIdsPerActor;
        if (!_state.MarkDead(ctx.RoomName, victim)) return RelayVerdict.Forward(ctx.Event);   // already dead -> debounce

        _bus?.PublishDeath(new DeathNotice(ctx.RoomName, victim));
        Log.Info("Killfeed", $"\"{ctx.RoomName}\": actor {victim} was eliminated");
        return RelayVerdict.Originate(ctx.Event, new List<EventData> { ServerRpc.Chat(victim, $"☠ Actor {victim} was eliminated") });
    }
}

/// <summary>Console command to toggle the kill feed live (Admin).</summary>
internal sealed class KillfeedCommands
{
    private readonly KillfeedState _state;
    public KillfeedCommands(KillfeedState state) => _state = state;

    [ConsoleCommand("killfeed", Usage = "[on|off]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1) return $"killfeed: {(_state.On ? "on" : "off")} (announces real deaths)";

        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "on": _state.On = true; return "killfeed: on";
            case "off": _state.On = false; return "killfeed: off";
            default: return "usage: killfeed [on|off]";
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter KillfeedDeath`
Expected: PASS (all five tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/Plugins/KillfeedPlugin.cs server/BlackIce.Server.Tests/KillfeedDeathTests.cs
git commit -m "feat(lb): killfeed detects real deaths (KilledPlayerRemote), retires HP-summing"
```

---

### Task 7: ArenaOptions — respawn toggle + spawn point

**Files:**
- Modify: `server/BlackIce.Server.Core/ArenaOptions.cs`

No new test file — the values are plain config consumed by the arena (covered by Task 8). This is a small,
self-contained config addition.

- [ ] **Step 1: Add the options**

In `server/BlackIce.Server.Core/ArenaOptions.cs`, add these properties inside the `ArenaOptions` class
(after `ResetOnWin`):

```csharp
    /// <summary>When true, on a round reset the server respawns every participant (sends the captured
    /// Teleport+BecomeTangible sequence) so the next round starts everyone alive.</summary>
    public bool RespawnAtReset { get; set; } = true;

    /// <summary>The world spawn point participants are respawned to at round reset. Defaults to the point
    /// captured live (the Co-op shop/base area). One point for all — per-team spawns are not yet captured.</summary>
    public float SpawnX { get; set; } = 520f;
    public float SpawnY { get; set; } = 3f;
    public float SpawnZ { get; set; } = 469.5f;
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build server/BlackIce.Server.Core/BlackIce.Server.Core.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add server/BlackIce.Server.Core/ArenaOptions.cs
git commit -m "feat(core): ArenaOptions respawn toggle + spawn point"
```

---

### Task 8: Arena — death-based scoring + respawn at reset

The arena now scores on the `Died` channel (victim's opposing team gains a point) and, on round reset,
orchestrates respawn for every room participant.

**Files:**
- Modify: `server/BlackIce.Server.LoadBalancing/Plugins/ArenaPlugin.cs`
- Test: `server/BlackIce.Server.Tests/ArenaMatchTests.cs`

- [ ] **Step 1: Write the failing test**

Create `server/BlackIce.Server.Tests/ArenaMatchTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests;

public class ArenaMatchTests
{
    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void A_death_scores_the_victims_opposing_team()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.TeamVsTeam);
        int victimTeam = modes.AssignTeam("co-op", 6);          // team 0
        var state = new ArenaState { Enabled = true, ScoreCap = 25 };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));

        Assert.Equal(1, state.Score("co-op", 1 - victimTeam));   // opponent scored
        Assert.Equal(0, state.Score("co-op", victimTeam));
    }

    [Fact]
    public void Reaching_the_cap_wins_and_resets_the_score()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.TeamVsTeam);
        int victimTeam = modes.AssignTeam("co-op", 6);
        var state = new ArenaState { Enabled = true, ScoreCap = 1, ResetOnWin = true };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));               // opponent hits the cap -> win -> reset

        Assert.Equal(0, state.Score("co-op", 1 - victimTeam));   // reset cleared the score
        Assert.False(state.Ended("co-op"));
    }

    [Fact]
    public void Non_team_modes_do_not_score()
    {
        var modes = new GameModeRegistry();
        modes.SetMode("co-op", GameMode.Coop);
        var state = new ArenaState { Enabled = true };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnDeath(new DeathNotice("co-op", 6));

        Assert.Equal(0, state.Score("co-op", 0));
        Assert.Equal(0, state.Score("co-op", 1));
    }

    [Fact]
    public void RespawnAll_sends_teleport_and_tangible_for_every_participant()
    {
        var state = new ArenaState { SpawnX = 520f, SpawnY = 3f, SpawnZ = 469.5f };
        var match = new ArenaMatch(state, modes: null, rooms: null, bus: null);
        var session = new RoomSession("co-op", new InterceptorChain(System.Array.Empty<IEventInterceptor>()));
        var p6 = Peer(out var r6); session.Join(6, p6);
        var p7 = Peer(out var r7); session.Join(7, p7);

        match.RespawnAll(session);

        // Each participant gets a Teleport + BecomeTangible; every member receives them (4 events total).
        foreach (var raised in new[] { r6, r7 })
        {
            Assert.Equal(4, raised.Count);
            var methods = raised.Select(e =>
                (string)((System.Collections.IDictionary)e.Parameters[PhotonCodes.Param.Data])[PhotonCodes.RpcKey.MethodName]!).ToList();
            Assert.Equal(2, methods.Count(m => m == "TeleportImmediately"));
            Assert.Equal(2, methods.Count(m => m == "BecomeTangible"));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter ArenaMatch`
Expected: FAIL — `ArenaMatch.OnDeath`, `ArenaMatch.RespawnAll`, and `ArenaState.SpawnX/SpawnY/SpawnZ` don't exist (compile errors).

- [ ] **Step 3: Rewrite the plugin**

Replace the entire contents of `server/BlackIce.Server.LoadBalancing/Plugins/ArenaPlugin.cs` with:

```csharp
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BlackIce.Server.Core;
using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin that turns a Team-vs-Team realm into a scored, replayable <b>arena match</b>, entirely
/// server-side. It scores on real player deaths the <c>killfeed</c> plugin publishes on the
/// <see cref="KillBus"/>: each death credits the victim's <b>opposing</b> team a point (the death RPC
/// carries no killer, so scoring is death-based), the running score is broadcast to the room (vanilla
/// chat), and the first team to <see cref="ArenaOptions.ScoreCap"/> wins. When
/// <see cref="ArenaOptions.ResetOnWin"/> is set the match resets and — when
/// <see cref="ArenaOptions.RespawnAtReset"/> is set — every participant is respawned (the captured
/// Teleport+BecomeTangible sequence) so the next round starts clean. Off by default; requires the
/// <c>killfeed</c> plugin enabled (its death source) and a Team-vs-Team realm.
/// </summary>
public sealed class ArenaPlugin : IServerPlugin
{
    public string Name => "arena";
    public string Description => "Team-deathmatch arena for Team-vs-Team realms: scores real deaths, first team to the cap wins, then resets and respawns. Off by default.";

    public void Configure(PluginBuilder builder)
    {
        var opt = (ArenaOptions?)builder.Services.GetService(typeof(ArenaOptions)) ?? new ArenaOptions();
        var state = new ArenaState
        {
            Enabled = opt.Enabled, ScoreCap = opt.ScoreCap, ResetOnWin = opt.ResetOnWin,
            RespawnAtReset = opt.RespawnAtReset, SpawnX = opt.SpawnX, SpawnY = opt.SpawnY, SpawnZ = opt.SpawnZ,
        };
        var modes = (GameModeRegistry?)builder.Services.GetService(typeof(GameModeRegistry));
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var bus = (KillBus?)builder.Services.GetService(typeof(KillBus));

        var match = new ArenaMatch(state, modes, rooms, bus);
        if (bus is not null) bus.Died += match.OnDeath;   // score on every published real death

        builder.AddCommands(new ArenaCommands(state, match));
    }
}

/// <summary>Live-tunable arena settings plus per-(room, team) scores and a per-room "match over" flag.</summary>
internal sealed class ArenaState
{
    public bool Enabled;
    public int ScoreCap = 25;
    public bool ResetOnWin = true;
    public bool RespawnAtReset = true;
    public float SpawnX = 520f, SpawnY = 3f, SpawnZ = 469.5f;

    private readonly ConcurrentDictionary<(string Room, int Team), int> _score = new();
    private readonly ConcurrentDictionary<string, bool> _ended = new();   // room -> match decided, awaiting reset

    public int Add(string room, int team) => _score.AddOrUpdate((room, team), 1, (_, v) => v + 1);
    public int Score(string room, int team) => _score.GetValueOrDefault((room, team));
    public bool Ended(string room) => _ended.GetValueOrDefault(room);
    public void MarkEnded(string room) => _ended[room] = true;

    public void ResetRoom(string room)
    {
        foreach (var key in _score.Keys.Where(k => k.Room == room).ToArray()) _score.TryRemove(key, out _);
        _ended.TryRemove(room, out _);
    }

    /// <summary>Rooms that currently hold any score (for the console reset-all and status).</summary>
    public IReadOnlyList<string> ActiveRooms() => _score.Keys.Select(k => k.Room).Distinct().ToList();
}

/// <summary>The match logic: reacts to published real deaths, scores the opposing team, declares a winner
/// at the cap, resets, and (when enabled) respawns participants. Runs on the Game listener thread (the kill
/// bus fires from the relay), so its broadcasts and state changes are single-threaded per listener.</summary>
internal sealed class ArenaMatch
{
    private readonly ArenaState _state;
    private readonly GameModeRegistry? _modes;
    private readonly RoomRegistry? _rooms;
    private readonly KillBus? _bus;

    public ArenaMatch(ArenaState state, GameModeRegistry? modes, RoomRegistry? rooms, KillBus? bus)
    {
        _state = state;
        _modes = modes;
        _rooms = rooms;
        _bus = bus;
    }

    public void OnDeath(DeathNotice n)
    {
        if (!_state.Enabled || _state.Ended(n.Room)) return;
        if (_modes?.ModeOf(n.Room) != GameMode.TeamVsTeam) return;
        if (_modes.TeamOf(n.Room, n.Victim) is not int victimTeam) return;

        int scoringTeam = 1 - victimTeam;
        int score = _state.Add(n.Room, scoringTeam);
        Announce(n.Room, n.Victim, $"⚔ Team {Name(scoringTeam)} scores — {ScoreLine(n.Room)} (first to {_state.ScoreCap})");

        if (score >= _state.ScoreCap)
        {
            int loser = 1 - scoringTeam;
            Announce(n.Room, n.Victim,
                $"\U0001F3C6 Team {Name(scoringTeam)} WINS {_state.Score(n.Room, scoringTeam)}–{_state.Score(n.Room, loser)} — Team {Name(loser)} loses!");
            if (_state.ResetOnWin) Reset(n.Room, n.Victim);
            else _state.MarkEnded(n.Room);
        }
    }

    /// <summary>Resets a room's match: clears scores, wipes the killfeed dead-set (via the bus), starts a new
    /// round, and (when enabled) respawns every participant.</summary>
    public void Reset(string room, int? announceActor = null)
    {
        _state.ResetRoom(room);
        _bus?.RequestReset(room);   // tell killfeed to clear this room's dead-set
        var session = _rooms?.FindSession(room);
        int actor = announceActor ?? session?.Actors().FirstOrDefault() ?? 0;
        Announce(room, actor, "\U0001F504 New round — fight!");
        if (_state.RespawnAtReset && session is not null) RespawnAll(session);
    }

    /// <summary>Respawns every current participant to the configured spawn point, sending the captured
    /// Teleport+BecomeTangible sequence for each so all clients replicate it.</summary>
    internal void RespawnAll(RoomSession session)
    {
        // GAP: per-team spawn points uncaptured — everyone respawns to one configurable point.
        foreach (var actor in session.Actors())
        {
            session.SendToAll(ServerRpc.Teleport(actor, _state.SpawnX, _state.SpawnY, _state.SpawnZ));
            session.SendToAll(ServerRpc.BecomeTangible(actor));
        }
    }

    /// <summary>Resets every room that currently has a score (console <c>arena reset</c>).</summary>
    public int ResetAll()
    {
        var rooms = _state.ActiveRooms();
        foreach (var room in rooms) Reset(room);
        return rooms.Count;
    }

    private void Announce(string room, int actor, string text)
    {
        _rooms?.FindSession(room)?.SendToAll(ServerRpc.Chat(actor, text));
        Log.Info("Arena", $"\"{room}\": {text}");
    }

    private string ScoreLine(string room) => $"Team A {_state.Score(room, 0)} – Team B {_state.Score(room, 1)}";
    private static char Name(int team) => (char)('A' + team);
}

/// <summary>Console commands to inspect and run the arena match live (Admin).</summary>
internal sealed class ArenaCommands
{
    private readonly ArenaState _state;
    private readonly ArenaMatch _match;
    public ArenaCommands(ArenaState state, ArenaMatch match)
    {
        _state = state;
        _match = match;
    }

    [ConsoleCommand("arena", Usage = "[on|off|scorecap <n>|reset]", MinLevel = PlayerLevel.Admin)]
    private string Cmd(CommandLine line)
    {
        if (line.Parts.Count == 1)
            return $"arena: {(_state.Enabled ? "on" : "off")}, first to {_state.ScoreCap}, reset-on-win {(_state.ResetOnWin ? "on" : "off")}, " +
                   $"respawn-at-reset {(_state.RespawnAtReset ? "on" : "off")} (scores real deaths in Team-vs-Team realms; needs the killfeed plugin on)";

        var verb = line.Parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "on": _state.Enabled = true; return $"arena: on — first team to {_state.ScoreCap} wins";
            case "off": _state.Enabled = false; return "arena: off";
            case "scorecap":
                if (line.Parts.Count >= 3 && int.TryParse(line.Parts[2], out var cap) && cap >= 1)
                {
                    _state.ScoreCap = cap;
                    return $"arena: score cap {cap}";
                }
                return "usage: arena scorecap <n>   (n >= 1)";
            case "reset":
                int n = _match.ResetAll();
                return n > 0 ? $"arena: reset {n} room(s)" : "arena: no active matches to reset";
            default:
                return "usage: arena [on|off|scorecap <n>|reset]";
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter ArenaMatch`
Expected: PASS (all four tests).

- [ ] **Step 5: Commit**

```bash
git add server/BlackIce.Server.LoadBalancing/Plugins/ArenaPlugin.cs server/BlackIce.Server.Tests/ArenaMatchTests.cs
git commit -m "feat(lb): arena scores real deaths and respawns at round reset"
```

---

### Task 9: Integration — full build, suite, format

No new code; this verifies the whole system holds together (both plugins are auto-discovered via
`PluginLoader.BuiltIn()` reflection and already resolve `KillBus` from DI, so no new wiring is needed).

- [ ] **Step 1: Confirm both plugins still discover**

Run: `dotnet test server/BlackIce.Server.sln --filter Built_in_plugins_are_discovered`
Expected: PASS (the existing discovery test; `killfeed` and `arena` remain registered `IServerPlugin`s).

- [ ] **Step 2: Full solution build**

(Stop the dev server first if running — it locks `BlackIce.Photon.dll`.)
Run: `dotnet build server/BlackIce.Server.sln`
Expected: Build succeeded, **0 warnings, 0 errors**. Fix any warning that appears.

- [ ] **Step 3: Full test suite**

Run: `dotnet test server/BlackIce.Server.sln`
Expected: all tests pass (the prior suite plus the new `RpcShortcuts`, `PhotonCustomData`, `PunRpc`
(updated), `KillBus`, `ServerRpc`, `KillfeedDeath`, `ArenaMatch` tests). No failures, no skips beyond the
oracle-gated ones.

- [ ] **Step 4: Style check**

Run: `dotnet format server/BlackIce.Server.sln --verify-no-changes`
Expected: no changes required. If it reports drift, run `dotnet format server/BlackIce.Server.sln` and
include the result.

- [ ] **Step 5: Gap-inventory check**

Run: `git grep -nE "// (STUB|GAP):" server/BlackIce.Server.LoadBalancing/Plugins/ArenaPlugin.cs`
Expected: the one intended `// GAP:` (per-team spawn points). Confirm no stray `// STUB:` was introduced.

- [ ] **Step 6: Commit any format/cleanup**

```bash
git add -A
git commit -m "chore(lb): format + cleanup after arena down-and-respawn"
```

(Skip if Steps 2–5 produced no changes.)

---

## Self-Review

**1. Spec coverage** — every spec component maps to a task:
- `RpcShortcuts` table → Task 2. `PunRpcInfo` shortcut resolution + `Args` → Task 3. `KillBus.Died`/`DeathNotice` → Task 4. `ServerRpc.Teleport`/`BecomeTangible` → Task 5. `killfeed` real-death detector (retire HP-summing) → Task 6. `ArenaOptions` spawn point → Task 7. `arena` death-based scoring + respawn at reset → Task 8. Promoted `PhotonCustomData.Vector3` factory (DRY) → Task 1. Integration/wiring → Task 9.
- Spec edge cases: self/environmental death scored against victim's team (Task 8 `OnDeath` — no killer needed); duplicate `KilledPlayerRemote` debounced (Task 6 `MarkDead`); victim with no team announced-not-scored (Task 6 announces, Task 8 `OnDeath` returns on null `TeamOf`); respawn to a live player harmless (only at reset, Task 8).
- Spec gaps recorded in code: per-team spawn points (`// GAP:` in Task 8 `RespawnAll`); kill-credit deferred (`KillBus.Killed` retained, Task 4); pawn viewID convention (see note below).

**2. Placeholder scan** — no TBD/TODO/"handle errors"/"similar to". Every code step shows complete code; every run step shows the exact command and expected result.

**3. Type consistency** — checked across tasks: `DeathNotice(string Room, int Victim)` (Tasks 4/6/8); `PunRpcInfo(..., int? MethodIndex, object[]? Args)` (Task 3) consumed as `rpc.Args[0]` (Task 6); `KillfeedInterceptor(KillfeedState, KillBus?)` ctor (Task 6) matches the test (Task 6 Step 1); `KillfeedState.MarkDead/Forget/ClearDead` consistent (Task 6); `ArenaMatch(ArenaState, GameModeRegistry?, RoomRegistry?, KillBus?)` ctor + `OnDeath`/`Reset`/`RespawnAll` (Task 8) match the tests; `ArenaState.SpawnX/SpawnY/SpawnZ/RespawnAtReset` (Task 8) match `ArenaOptions` (Task 7); `PhotonCustomData.Vector3` (Task 1) used by `ServerRpc.Teleport` (Task 5) and `GameActions.Vec3` (Task 1); `ServerRpc.Teleport/BecomeTangible` (Task 5) used by `RespawnAll` (Task 8) and asserted by `ServerRpcTests` (Task 5). `RpcShortcuts.Name`/`Methods` (Task 2) used by `PunRpcInfo` (Task 3).

**Note (pawn viewID convention):** `victim = viewId / 1000` (Task 6) and `actor*1000+1` (Task 5) reflect the observed convention already encoded in `ServerRpc.Chat` and `GameModeTests.PlayerDamage`. It is an established assumption in the codebase, documented in `live-verification.md`; not re-litigated here.
