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
