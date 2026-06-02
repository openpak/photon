# Real Arena Down-and-Respawn — Design

**Status:** approved (brainstorm) — ready for implementation plan
**Date:** 2026-06-02
**Phase context:** Phase 2/3 gameplay (server-side arena), builds on the live protocol-verification pass

## Goal

Replace the `killfeed`/`arena` plugins' HP-summing kill *approximation* with detection of **true player
deaths** from the captured death RPC, score the match **death-based**, and **orchestrate respawns at round
reset** using the captured respawn RPC sequence. Entirely server-side, Steam-free, testable without the
game DLL.

## Why this is necessary (not just nicer)

The live capture (`docs/protocol/03-rpc-catalog.md`, `live-verification.md`) established two facts that
force this change:

1. **Damage is master-authoritative.** No `TakeDamage` RPC transits the wire in co-op — the master
   resolves damage locally and broadcasts only the outcome. The current `killfeed` interceptor keys off
   `PunRpcInfo.DamageValue` from `TakeDamage`, so **it never fires** in a real session. Its HP model is
   dead code.
2. **The real player death/respawn sequence is known:**
   - **Death:** `Shatter(59)` → `GoIntangible(27)` → `KilledPlayerRemote(32, arg = victim pawn viewID)`
   - **Respawn:** `TeleportImmediately(66, Vector3 spawnPos)` → `BecomeTangible(9)`
   - No `SetHealth` is broadcast (health is owner-local). Player death is a **different RPC path** from
     enemy death (`Die`/`SetDying`).

`KilledPlayerRemote` is the real, working death signal. It carries **only the victim viewID — no killer**
(the lava death was self-sent). That single constraint drives the scoring decision below.

## Locked decisions (from brainstorming)

- **Scoring model: death-based.** Each death credits the victim's **opposing** team `+1` (TvT, teams
  0/1). No killer attribution is required, so this works today. Kill-credit is a future enhancement once
  a PvP-killer capture confirms attribution (`KillBus.Killed`/`KillNotice` are kept for it).
- **Respawn role: orchestrate at round reset only.** During a round, deaths are detected passively (real
  players self-respawn via the game). On reset (score cap reached), the server sends the captured
  respawn sequence to **all participants** so the new round starts everyone alive.
- **Keep the `killfeed` command/plugin name** (now feeding on real deaths) for continuity.
- **Retire the HP-summing entirely** — it is dead given no `TakeDamage`.
- **Respawn target: all participants → one spawn point** (per-team spawns uncaptured; configurable).

## Architecture

Reuses the existing two-plugin split and their shared `KillBus`:

- `killfeed` — **detects** real deaths from `KilledPlayerRemote` and **announces** eliminations.
- `arena` — **scores** death-based, declares the winner at the cap, **resets** the round, and at reset
  **orchestrates respawn**.

```
client → KilledPlayerRemote(victim)        (relayed RPC, event 200)
       → killfeed interceptor              (matches the death RPC)
       → KillBus.Died (DeathNotice)        (in-process pub/sub)
       → arena scores opponent team        (TvT, +1)
       → announce score (vanilla chat)
       → at cap: announce win → reset
       → server sends TeleportImmediately + BecomeTangible to all participants (revive round)
```

## Components

### 1. `RpcShortcuts` — new, `server/BlackIce.Photon/RpcShortcuts.cs`

The 88-entry shortcut **index → method name** table (source of truth in code, mirroring
`docs/protocol/generated/rpc-shortcuts.csv`). PUN sends frequently-used RPCs as a byte index into this
list (Photon RPC key 5) rather than by name; the client sends `KilledPlayerRemote` as index `32`.

- `static IReadOnlyList<string> Methods` — the ordered list (index = position).
- `static string? Name(int index)` — index → method name, or null if out of range.
- Lives in `BlackIce.Photon` next to `PhotonCodes` (protocol data).
- **Caveat (documented):** if a game update reorders the list, regenerate from a fresh
  `BlackIce.OpLogger` `rpclist` capture. Indices are not stable across game versions.

### 2. `PunRpcInfo` — extend, `server/BlackIce.Photon/PunRpcInfo.cs`

Today `Method` is null when the RPC is sent as a shortcut index — which is exactly how the death RPC
arrives, so detection is impossible without this change.

- Read key 5 (`PhotonCodes.RpcKey.MethodShortcut`) when present.
- Populate `Method` by resolving the index through `RpcShortcuts.Name(index)`, so `Method` is set whether
  the RPC was sent by name (key 3) or by shortcut (key 5).
- Add `int? MethodIndex` for callers that want the raw index.
- The existing `ViewId`/`DamageValue`/`DamagePacket` fields are unchanged (damage stays available for the
  future kill-credit path).

### 3. `KillBus` — extend, `server/BlackIce.Server.LoadBalancing/KillBus.cs`

Add a death channel alongside the existing kill channel:

- `readonly record struct DeathNotice(string Room, int Victim)`.
- `event Action<DeathNotice>? Died;`
- `void PublishDeath(DeathNotice notice) => Died?.Invoke(notice);`
- Keep `Killed`/`KillNotice`/`Publish` and `RoomReset`/`RequestReset` as-is.

### 4. `killfeed` plugin — repurpose, `.../Plugins/KillfeedPlugin.cs`

Becomes a **real-death detector + announcer**. Retire the HP-summing.

- **Interceptor:** for each inbound event, decode via `PunRpcInfo.From`. If `Method ==
  "KilledPlayerRemote"`, read the victim pawn viewID from the RPC args (`args[0]` as int), compute victim
  actor = `viewId / 1000`, debounce per `(room, victim)`, then:
  - `KillBus.PublishDeath(new DeathNotice(room, victim))`
  - announce `☠ Actor {victim} was eliminated` on vanilla chat (`RelayVerdict.Originate`).
  - Always forward the original death RPC (clients still need it).
- **Debounce:** a `HashSet<(room, victim)>` "currently dead" set; ignore repeat `KilledPlayerRemote` for
  an already-dead victim until cleared. Cleared on `KillBus.RoomReset` (round reset) and on actor-left.
- **State simplification:** remove `_damageTaken`, `_streak`, `AssumedMaxHp`, `StreakAnnounceThreshold`,
  and the `killfeed hp <n>` command (no killstreaks without killer attribution). Keep `killfeed
  [on|off]`. Update the plugin description to "real-death elimination feed."
- `Order` stays `100` (after validators).

### 5. `arena` plugin — evolve, `.../Plugins/ArenaPlugin.cs`

- Subscribe to `KillBus.Died` instead of `Killed`. New `OnDeath(DeathNotice)`:
  - gates as today: `Enabled`, not `Ended`, mode is `TeamVsTeam`.
  - `victimTeam = modes.TeamOf(room, victim)`; if null, ignore (announced by killfeed, not scored).
  - `scoringTeam = 1 - victimTeam`; `score = state.Add(room, scoringTeam)`; announce the score line.
  - at `ScoreCap`: announce win; if `ResetOnWin` reset, else `MarkEnded`.
- `Reset(room)`: clears scores, `RequestReset` (clears killfeed dead-set), announces "new round", and —
  when `ArenaOptions.RespawnAtReset` — **orchestrates respawn**: for each participant (room actors +
  team-assigned bots via `modes`), send `ServerRpc.Teleport(actor, SpawnPoint)` then
  `ServerRpc.BecomeTangible(actor)` through the room session.
- `ArenaCommands` unchanged except help text.

### 6. `ServerRpc` — extend, `.../ServerRpc.cs`

Add server-authored respawn builders (by method name, targeting `actor*1000+1`, like `Chat`):

- `EventData Teleport(int actor, float x, float y, float z)` — `TeleportImmediately(Vector3)`.
- `EventData BecomeTangible(int actor)` — `BecomeTangible()` (no args).

**Reuse the existing Vector3 encoder, don't duplicate it.** `GameActions.Vec3(x,y,z)` already builds a
`PhotonCustomData(CustomType.Vector3, …)` (12 bytes, big-endian). Promote it to a single shared factory —
`PhotonCustomData.Vector3(x, y, z)` in `BlackIce.Photon` — and have **both** `GameActions` and
`ServerRpc.Teleport` call it. This is a small DRY refactor folded into this work (anti-bloat: the encoder
exists; we centralize rather than re-write it).

### 7. `ArenaOptions` — extend, `server/BlackIce.Server.Core/ArenaOptions.cs`

- `bool RespawnAtReset { get; set; } = true;`
- `float SpawnX/SpawnY/SpawnZ` defaulting to the captured spawn point `520, 3, 469.5`.
- `Validate()` unchanged (the spawn point is free-form; no constraint).

## Error handling

- **Death RPC with unreadable/missing victim arg** → ignore (log at Debug); never throw on bad input
  (matches the relay's defensive decode).
- **Self/environmental death** (sender == victim) → still a valid death; scored against the victim's
  team. Death-based scoring is killer-agnostic.
- **Victim has no team** → announced by killfeed, not scored (TvT-only arena, as today).
- **Respawn to an already-alive player** → harmless re-warp; only occurs at round reset.
- **Unresolvable shortcut index** (table drift after a game update) → `Method` resolves to null, the death
  is simply not detected; documented caveat, not a crash.

## Known gaps (markers in code)

- `// GAP:` per-team spawn points uncaptured — v1 respawns all participants to one configurable point.
- `// GAP:` pawn viewID convention `actor*1000+1` is observed, not formally documented.
- Kill-*credit* (who killed whom) deferred to a future PvP-killer capture; `KillBus.Killed` retained for
  it.

## Testing (xUnit, Steam-free, no game DLL)

- **`RpcShortcuts`**: `Name(32) == "KilledPlayerRemote"`, `Name(39) == "ReceiveChatMessage"`, out-of-range
  → null.
- **`PunRpcInfo`**: an event whose RPC table has key 5 = 32 resolves `Method == "KilledPlayerRemote"` and
  `MethodIndex == 32`; a name-sent RPC (key 3) still resolves; a non-RPC event → null.
- **`killfeed` interceptor**: a `KilledPlayerRemote` event publishes one `DeathNotice` with victim actor =
  `arg0 / 1000` and originates an elimination chat line; a non-death RPC publishes nothing; a repeat death
  for an already-dead victim is debounced (one notice).
- **`arena`**: a `DeathNotice` for a teamed victim scores the opposing team; reaching `ScoreCap` announces
  a win and (with `ResetOnWin`) resets; on reset with `RespawnAtReset`, respawn RPCs
  (`TeleportImmediately` + `BecomeTangible`) are originated for every participant.
- **`ServerRpc`**: `Teleport`/`BecomeTangible` produce `EventData` with the correct event code, target
  viewID (`actor*1000+1`), method name, and (for `Teleport`) a Vector3 custom-type arg.

## Anti-bloat notes

- Net code likely **shrinks**: retiring the HP-summing/streak model removes more than the death detector
  adds.
- `RpcShortcuts` is justified — durable protocol data with multiple present/future consumers (death
  detection, the chat-command shortcut heuristic, future authority checks).
- No new plugins, no new DI wiring beyond the existing `KillBus`; reuses `IEventInterceptor`,
  `RelayVerdict`, `ServerRpc`, `RoomRegistry`, `GameModeRegistry`.
