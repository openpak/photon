# Live-verification checklist

This file collects every protocol/behavior fact the clean-room server currently **assumes or
approximates** and that should be **confirmed against a real copy of the game** — the
`Photon3Unity3D.dll` interop oracle (test-only, gitignored) and/or a live op-log capture. Nothing here
is a known bug; these are the places where the docs/recon stopped short of pinning a value, so the code
makes the most defensible choice and flags it. Confirm, then delete the corresponding row.

Legend: **[codec]** affects wire compatibility · **[ac]** affects an anti-cheat decision · **[mode]**
affects custom game modes · **[cosmetic]** diagnostic/logging only.

## Serialization / custom-type layouts

| Item | What the server assumes today | How to verify | Impact |
|---|---|---|---|
| **DamagePacket.Damage type** | Read as a **big-endian float** at offset 0 (`PunRpcInfo`), and now also **written** there by `DamageData` (the single definition of the packet shared by the bots, the gameplay plugins, and the anti-cheat). The recon doc (`04-serialization.md`) calls it an **int32**. The two disagree; the code treats it as float — a capture validates `DamageData`. | Capture a real `TakeDamage` RPC; decode offset 0 both ways and see which yields a sane damage value. | [ac][codec] |
| **DamagePacket headshot/weak-point flag** | "combined" bitfield at **byte 36** (big-endian int32) with Crit=bit0, **WeakPoint=bit1**; the anti-cheat reads **offset 39, mask 0x02** (the big-endian LSB) as the weak-point/"headshot" bit. | Capture weak-point vs body hits; confirm which byte/bit toggles. Set `Anticheat.HeadshotFlagOffset`/`HeadshotFlagMask` accordingly. | [ac] |
| **DamagePacket Originator/Faction offsets** | Documented as int32 @4 (Originator) and @8 (Faction); **not yet consumed** by the server. The Team-vs-Team mode trusts the server's own team map, not these fields. | Capture and confirm offsets before trusting client-sent faction. | [mode][ac] |
| **XPPacket (custom type 88)** | Layout **unknown**; bots send a placeholder int arg, not the real packet. | Capture an `AddXP`/`AddXPRPC`; map fields. | [codec] |
| **AffixPacket (custom type 65)** | Layout **unknown**; not constructed. | Capture a projectile/loot RPC carrying it. | [codec] |
| **Quaternion field order** | Written/parsed as **(x,y,z,w)** big-endian. PUN's `CustomTypes` is documented (elsewhere) as **(w,x,y,z)**. The bots send identity quaternions so order is moot for them, but rotation validation would need the right order. | Round-trip a known rotation through the oracle. | [codec] |
| **SendSerialize (201) tail fields** | After Vector3+Quaternion the server treats the per-view tail as `damageTaken, maxHealth, tempHP, headPitch` (from `BotManager`/recon). Field count/order **inferred, not formally documented**. | Capture a real `OnPhotonSerializeView` for the Player prefab. | [codec][ac] |
| **Destroy (204) payload** | Server best-effort reads a viewID at key 7, else scans for a cached viewID. Exact 204 layout **not documented**. | Capture a `PhotonNetwork.Destroy`. | [codec] |
| **Instantiation (202) caching scope** | Server caches all 202s as room state and replays to late joiners; which events the real client expects cached vs transient is **not documented**. | Compare against Photon's `EventCaching` use in a capture. | [codec] |

## Photon LoadBalancing constant names (diagnostic)

The Photon-protocol research flagged a few names in `PhotonNames` that may be mislabeled vs canonical
Photon (these are **logging-only** — they don't change dispatch, which keys on the numeric code):

| Code | `PhotonNames` label | Canonical (per research) | 
|---|---|---|
| Op 217 / 221 / 224 | JoinRandomOrCreate / GetGameList / CancelJoinRandom | likely GetGameList=217, …, JoinRandomOrCreate=224 |
| Event 226 / 210 / 230 | ErrorInfo / CacheSliceChanged / AuthOrGameList | canonical ErrorInfo=251, CacheSliceChanged=250, AuthEvent=223 |
| Param 252 / 247 / 236 / 235 | ActorList/CacheOp / Cache / PluginName / PluginVersion | Actors=252, Cache=247, EmptyRoomTTL=236, PlayerTTL=235 |

Verify against an oracle capture, then correct the `switch` arms in `PhotonNames`. **[cosmetic]**

## RPC handling

| Item | Assumption | Verify | Impact |
|---|---|---|---|
| **RPC shortcut index (key 5)** | The server only resolves RPC **method names** (key 3); when a client sends the **shortcut byte index** (key 5) instead, the method is treated as unknown (`<shortcut rpc>`). Chat-command interception already handles the shortcut case heuristically. | Capture the client's `RpcShortcuts` ordering to map index→name. | [ac] |
| **Which RPCs the real client raises** | The reserved-event-code guard blocks clients raising Join/Leave/PropertiesChanged (253/254/255). Assumes a legit client never raises those via `OpRaiseEvent`. | Confirm in a capture that no normal RPC uses those event codes. | [ac] |
| **Player-damage vs enemy-damage discrimination** | Both ride `TakeDamage`; the server distinguishes by whether the target viewID's block maps to a **player actor in the room** (player-damage) vs a scene/enemy object. | Confirm enemy viewIDs really fall outside player blocks (scene = block 0). | [mode][ac] |
| **Death / respawn RPC** | The kill model in the `killfeed` / `arena` plugins is a server-side **approximation**: it sums relayed `TakeDamage` toward an assumed max-HP and credits a "kill" when the total crosses it, but **no real death or respawn RPC is mapped**, so the client never actually downs or respawns — the arena scores while combatants keep fighting unbroken, and a match "reset" only clears the scoreboard. | Capture a real player death and the respawn that follows: the RPC/event name(s), their args, and the HP/position reset sequence the client expects. Then the arena can detect **true** deaths (instead of modelling HP) and **down-and-respawn** players/bots — a real arena round — rather than only scoring. | [mode][ac] |

## Game-mechanic / enum gaps (from the RPC catalog)

`LaserSoundIndex`, `LaserSoundType`, `ParticleIndex`, `SoundIndex` (AOE), `PayloadIndex`
(GrapplingHook), `IndustryType` (EnemyAI), the `Player` custom type (Disco RPCs), and the
**hack node state machine** (which `SetupHack`/`EndHack`/`FinishEndingHack` transition represents which
state, and the meaning of their numeric args) are all **undocumented**. The soak bots send well-formed
but placeholder values for these. Capture real instances to pin them down. **[codec]**

## Server-side scope decisions (deliberate, not gaps)

- **Wire codec stays exception-based.** The GpBinary codec / `WireMessage` signal malformed input by
  throwing (caught at the listener boundary), rather than returning `Result`. This path is the one I
  could not validate here without the oracle DLL; run `dotnet test server/BlackIce.Server.sln` with the
  DLL present to exercise the full round-trip. **[codec]**
- **Anti-cheat thresholds are detection-only by default** and generous; tune against live play before
  setting `Anticheat.Enforce`. **[ac]**
- **Hard kick** sends an eNet Disconnect + evicts server-side; whether the real client tears down
  immediately on that command (vs only on timeout) is unconfirmed. **[codec]**
