# RPC Catalog

85 `[PunRPC]` handlers extracted from `Assembly-CSharp.dll` into
[`generated/rpcs.csv`](generated/rpcs.csv) (declaring type, method, parameter type list,
authority hint). RPCs are dispatched via `RaiseEvent` (op 253) with the PUN RPC event code
and a target/buffering mode.

## Authority finding (central to the project)

The `ReferencesMasterClient` column is **`False` for all 85 RPCs**. RPC handlers are
*receivers*; PUN enforces master-client gating at the *caller* (`photonView.RPC(...)`),
not inside the handler. Consequently the handlers themselves validate nothing about the
sender — a client that emits a crafted `RaiseEvent` carrying, e.g., `EnemyNetworkDamage.Die`
or `InventoryControl` mutations is simply trusted. This is the exact surface Phase 3
(server authority) and Phase 4 (anti-cheat) must close.

## Shortcut-index table (key 5) — live-captured

PUN can send a frequently-used RPC as a **byte index** into the project's ordered RPC list (Photon RPC
key `5`, the "method shortcut") instead of the full method name (key `3`). That ordering lives in a Unity
asset (`PhotonServerSettings.RpcList`), not in code, so it cannot be resolved from decompilation — it has
to be read from a live client. The ordered table (88 entries) is captured in
[`generated/rpc-shortcuts.csv`](generated/rpc-shortcuts.csv) and resolves any `"5":N` seen in a capture
to a method name (e.g. `5:39` → `ReceiveChatMessage`, `5:63` → `TakeDamage`, `5:48` → `SetDying`). The
first ~74 entries are alphabetical; indices 74+ were appended as the game added features (so the order is
the asset's, not re-sortable). Re-dump it after a game update with `BlackIce.OpLogger` (it records a
`kind:"rpclist"` line on launch).

## Authority finding — now confirmed live

The static inference above (handlers trust the sender) is borne out by a live co-op capture. With a single
authoritative client in the room, **every** gameplay RPC was *outgoing* and none was received: the client
sent `SpawnProjectileRemote`, `SetDying`, `KillEnemyPhase`, `AddSpawnedEnemies`, `WakeEnemyAfterDelay`,
`SetupHack`/`FinishEndingHack`. Critically, **no `TakeDamage` RPC was ever sent** — the master computes
damage locally and broadcasts only the *outcome* (`SetDying`). So `DamagePacket` does not transit the wire
in a master-authoritative session; capturing its layout requires a **non-master** client taking damage (or
PvP). This is direct evidence for why Phase 3/4 must make the **server** the damage authority — a relay
that never sees the damage cannot validate it.

## Player death & respawn sequence — live-captured

The **player** death/respawn flow (distinct from the *enemy* `Die`/`SetDying` path) was captured from a
live client dying to environmental damage (lava) and respawning. The player's pawn is a PhotonView (e.g.
viewID `6001`); all six RPCs are sent by the dying client:

| Phase | RPC (index) | Target view | Args | Meaning |
|---|---|---|---|---|
| Death | `Shatter` (59) | pawn | — | death visual (pawn shatters) |
| Death | `GoIntangible` (27) | pawn | — | enter dead state (non-collidable) |
| Death | `KilledPlayerRemote` (32) | manager view | `victimViewID` | broadcast the death |
| Respawn | `TeleportImmediately` (66) | pawn | `Vector3 spawnPos` | warp to spawn point |
| Respawn | `BecomeTangible` (9) | pawn | — | back to alive (collidable) |

Notes: no `SetHealth` is broadcast on respawn — health is owner-local; the respawn is purely
teleport + re-enable collision. **Kill credit is not present** for an environmental death:
`KilledPlayerRemote` carried only the victim's viewID, no killer. PvP/enemy kill-credit (who killed whom)
needs a separate capture. This sequence lets the `arena`/`killfeed` plugins detect a **true** death
(`KilledPlayerRemote`) and respawn (`TeleportImmediately`+`BecomeTangible`) instead of modelling HP.

## By subsystem (counts from `rpcs.csv`)

| Subsystem | Type(s) | RPCs | Authority-sensitive examples |
|---|---|---|---|
| World / session | `WorldManager` (9), `DifficultyManager`, `FactionManager` | 11 | `KillEnemyPhase`, `KilledPlayerBuildingSecondary` |
| Enemy combat | `EnemyNetworkDamage` (9), `EnemyAI` (4), `EnemyAggroManager`, `EnemySpawner` | 15 | `TakeDamage`, `Die`, `SetDying`, `AddXP`, `KilledPlayer`, `AddAggro`, `AddImpact` |
| Player | `PlayerNetworkHelper` (8), `NetworkHelper` (3) | 11 | `TakeDamage` |
| Inventory / loot | `InventoryControl` (8), `NetworkLootCubeManager` (2), powerups (`Powerup` 5, `XPGemPowerup`, `HealthPowerup`) | 17 | item grants, `SetHealth`, `Die` |
| Projectiles / AOE | `ProjectileManager` (6), `AOEHelper` (2), `MineManager` (2), `ExplodingBarrel` | 11 | `TakeDamageOwner`, `KillProjectileOther`, `ExplodeObjects` |
| World objects | `LinkController` (3), `MovingPlatformTriggered` (2), `PrefabContainer` (2), `ParentHelper` (2), `TemporaryObjectsNetworkManager` (2) | 11 | `DieByViewID` |
| Status / misc | `ShieldManager` (2), `Cloaking` (2), `BuffManager` (2), `NetworkSyncPosition`, `DodgeballManager`, `ChatGUI` | 9 | shield/buff/cloak grants |

## Notable signatures

Several RPCs carry **custom serialized types** (see [04-serialization.md](04-serialization.md)):
- `ExplodingBarrel.TakeDamageOwner(DamagePacket, in AffixPacket)`
- `MineManager.TakeDamageOwner(DamagePacket, in AffixPacket)`
- `BuffManager.AddBuffRPC(Int32, Int32, Single, Int32, Single, Int32, PhotonMessageInfo)`

`PhotonMessageInfo` is appended by PUN automatically (sender, timestamp, photonView) and is
not sent on the wire — it is the natural place a server would learn the *claimed* sender for
validation.

## Highest-value anti-cheat targets (Phase 4 shortlist)

1. `EnemyNetworkDamage.TakeDamage` / `Die` / `AddXP` — damage and reward fabrication.
2. `PlayerNetworkHelper.TakeDamage` — invulnerability / remote-kill.
3. `InventoryControl.*` — item duplication / illegal grants.
4. `WorldManager.KillEnemyPhase` — progression spoofing.

Full per-method parameter lists: [`generated/rpcs.csv`](generated/rpcs.csv).
