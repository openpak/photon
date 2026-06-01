# Smart Bots — Design Spec (world-aware playerbots)

**Status:** Design, in progress on branch `feat/smart-bots` (autonomous iteration, 2026-05-31).
**Goal (owner):** Bots should act like real players — run around, find things to *hack* and *kill*,
equip/upgrade and get stronger, with collision so they don't clip through the world/objects.

## The hard architectural constraint (read first)

BlackIce.Server is a **clean-room relay**. It does **not** own the level mesh, navmesh, enemy AI, loot
tables, item effects, or progression rules — and it must not (extracting them would violate clean-room).
It only sees what the master client **relays**. Therefore:

| Owner ask | What we can actually do | Honesty |
|---|---|---|
| Bots run around the world | ✅ Navigate **between positions the master has spawned things at** | Real, achievable |
| Find & kill enemies | ✅ Target nearest relayed enemy, emit captured `TakeDamage`(DamagePacket) RPC | The master is authoritative over whether the kill *counts* — we emit the right wire shape |
| Find & hack nodes | ✅ Target relayed `Link` nodes, emit `SetupHack`→`FinishEndingHack` | Same authority caveat |
| Pick up loot | ✅ Target `NetworkLootCube`/`XPGem`/powerups, emit `RequestLootLock` | — |
| Equip / upgrade / get stronger | ⚠️ **Pseudo-progression**: bot tracks its own XP/level and fires `EquipWeapon`/`AddBuffRPC` on level-up | Server can't know item tables or what equips *do* — the number is ours, the RPC is real |
| **Collision / don't clip the world** | ⚠️ **Waypoint-on-spawns**, not physics | See below — this is the key idea |

### The collision answer: "the master's spawn points are the navmesh"

Every enemy / `Link` / loot the master instantiates arrives as a **PUN 202** carrying a **Vector3 at key 1**
— a *provably valid, reachable world position* (the game put something playable there). A bot that only ever
moves **toward observed spawn positions**, in bounded steps, and **holds position when it has no known
target** (rather than wandering blindly into geometry), stays on the graph of real in-world points. This is
not true collision — it's a clean-room-safe approximation that keeps bots out of walls without any level
geometry. True physics collision is **explicitly out of scope** (impossible server-side without the mesh).

## Architecture

```
master client ──(202 spawn / 204 destroy / 201 move)──▶ RoomSession.RelayFrom
                                                          └▶ interceptor chain
                                                             └▶ WorldStateObserver  ──writes──▶ RoomWorldState
                                                                                                  (shared, per room)
BotManager.Tick() (listener thread, 1 Hz) ──reads──▶ RoomWorldState ──▶ HunterBehavior.Think()
                                          ◀──move(201)+actions(200)── relays bot's intent through the session
```

### Components

| Component | Responsibility | Status |
|---|---|---|
| `RoomWorldState` (extend) | Entity gains `Kind` (prefab name) + last-known `X,Y,Z`; query helpers (`Alive()`, `Nearest(pred, x, z)`). | new fields/queries |
| `WorldStateObserver` (extend) | Parse prefab (key 0) + Vector3 (key 1) from the 202 and feed `ObserveSpawn(viewId, kind, x,y,z)`. | new parse |
| `RoomWorldStateRegistry` (new) | One shared `RoomWorldState` per room (DI singleton) so the authority plugin **and** the bot manager read the same view. | new |
| `IBotBrain` / `HunterBehavior` (new) | World-aware decision: pick nearest target, step toward it (waypoint), emit the matching captured RPC in range, track pseudo XP/level → equip/buff on level-up. | new |
| `BotManager` (extend) | Resolve the room's shared world-state; if a bot's behavior is an `IBotBrain`, drive `Think(world, self)` and relay its move + actions; else fall back to move-only `IBotBehavior`. | wire |

### Target taxonomy (classify by prefab name, from live capture)

- **Enemy** (attack): name contains `Enemy` (SpiderEnemy, CrabEnemy, ProbeEnemy) → `TakeDamage`(DamagePacket).
- **Hack node**: name == `Link` → `SetupHack` then, next ticks, `FinishEndingHack`.
- **Loot** (grab): `NetworkLootCube`, `XPGem`, name contains `Powerup` → `RequestLootLock`.
- Everything else (Player, ExplodingBarrel, scenery): ignored as a target.

### Pseudo-progression

Bot accrues XP per resolved interaction (kill/hack/loot). Crossing a per-level threshold raises its level and
emits a progression RPC (`EquipWeapon` / `AddBuffRPC`) — the *number* is the server's bookkeeping, the *RPC*
is a real captured shape. No claim is made that the game's economy actually advances; this is flavor that
makes the soak traffic look like a leveling player.

## Governing principles

- **Single listener thread.** Bots tick on the Game listener's maintenance pass (unchanged). The shared
  `RoomWorldState` backing maps stay concurrent as defense-in-depth (matching the authority layer).
- **Reuse captured RPC shapes.** The action wire-shapes already live in `GameActions` (verified against live
  capture). HunterBehavior fires them *contextually* instead of on a blind rotating script.
- **Determinism for tests.** The brain takes an injected RNG seed and is fully unit-testable against a
  hand-built `RoomWorldState` — no live server needed to verify targeting/movement/progression.
- **Opt-in & safe.** Smart bots are still gated by `Server.Bots.AutoSpawnPerRealm`; `CountInLobby` keeps them
  out of the advertised player count by default. The old `WanderBehavior`/`GameActions` soak path stays intact.

## Out of scope (deliberate)

- True physics collision / level geometry (clean-room impossible; waypoint-on-spawns is the approximation).
- Real game progression/economy (server doesn't own item tables).
- Bots as authoritative actors (the master client remains authority; we emit correct wire shapes and let it decide).
