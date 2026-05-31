# Phase 2 — Event Relay, Mod-Free Server Authority, and Playerbots

**Status:** design (approved 2026-05-30)
**Supersedes scope of:** the original "Phase 2 relay" + "Phase 3 server authority" placeholders, plus a new playerbot subsystem.

## Goal

Make a second actor real in the running game. Today the Game server accepts a client into a room
but relays **nothing** to other peers — `GameServerHandler.OnOperationRequest`'s RaiseEvent case
intentionally does not fan out. This phase builds the relay substrate so that movement, spawning,
damage, and death **replicate between clients**, layers a **mod-free server-authority seam** over
that relay, and adds **server-originated playerbots** (synthetic AI actors that need no client and
no real user).

A guiding constraint, set by the user: **avoid a client-side mod entirely** where possible — achieve
authority by controlling the event stream (WoW-emulator style), not by commanding the client. Where
mod-free authority is impossible, log/defer rather than require a mod now.

## Background: what the recon established (2026-05-30)

Three parallel recon agents read the decompiled client (`decompiled/`, gitignored, copyrighted —
never quoted) and the live trace log. Findings that shape this design (all paraphrased):

- **Damage is on the wire and rewritable.** When one entity damages another, the attacker's client
  computes a `DamagePacket` and sends it via a `TakeDamage` `[PunRPC]` to the victim's owner client;
  the victim then applies it locally (`IsMine`-gated). The packet is a registered Photon **custom
  type, code 68**, a fixed **41-byte** blob whose **first 4 bytes are the damage float**. RPCs ride
  Photon **event code 200** (single-target damage uses `TargetActors`, so it is relayed through us;
  scene-enemy damage targets the master client). → The relay can **read, clamp, zero, drop, or
  inject** damage **mod-free**.
- **Death is decided locally by the victim** (HP hits 0 → local `Die`, only cosmetic `Shatter`
  broadcast). There is no authoritative "kill" command on the wire to veto. → Death authority is
  enforced **indirectly** (control the damage so HP never illegitimately reaches 0); a *direct*
  death veto would need a client mod (deferred).
- **Movement is dumb-puppet replication.** Remote players run no local simulation (their
  `FirstPersonController` is disabled); a remote avatar only interpolates toward **absolute world
  positions** streamed via `OnPhotonSerializeView` on `NetworkSyncPosition`, as **unreliable event
  201** at ~10 Hz. → The server controls exactly what others see of a player and can **drop or
  clamp** that player's forwarded positions **mod-free**. A one-shot `TeleportImmediately` `[PunRPC]`
  exists and applies even to the owner, so a **rubber-band correction is mod-free**; but *continuous*
  correction of a cheater's own view is defeated by local input and would need a mod (deferred).
- **The server can be the master client and owns the ID namespace.** ViewIDs are
  `ownerActorNumber * 1000 + subId` (subId 1–999); the server assigns actor numbers, so it controls
  every client's viewID block. Spawns (enemies, world loot) are **master-client-gated**, and the
  relay sets which actor is master (Photon room param 248). → The server becomes the spawn/world
  authority by **claiming master**, mod-free, and can originate cached **event-202** instantiations.
- **Playerbots are fully feasible mod-free.** The exact post-join sequence a real player emits is:
  join (event **255**) → set identity properties (op **252**, all free-form: model index, four RGBA
  colors, name, level, team — **no client-side validation, no Steam binding, no actor-count
  cross-check**) → instantiate avatar (event **202**, viewID `actor*1000+1`, cached) → `RefreshModel`
  (event **200**) so peers pull appearance from custom properties → continuous position/gun stream
  (event **201/206**). The server can fabricate this sequence for a synthetic actor and an
  unmodified client renders it as a real player.

Caveats recorded for honesty: a client always renders its **own** locally-spawned objects regardless
of the server (the relay only governs what *others* see); hazard/fall/self-damage never crosses the
relay (can't be intercepted mod-free); RPC methods in the project's `RpcList` arrive as a **byte
shortcut at key 5**, not a name string at key 3 (the relay's classifier must resolve shortcuts — same
issue already handled for `/motd`).

## Architecture

One new pipeline in the Game server replaces the no-op RaiseEvent handling:

```
inbound (RaiseEvent 253 / SetProperties 252 / join / leave) from actor A in room R
        ↓  decode (codec layer)
   IEventInterceptor chain  ── ordered, per-event-type
        ↓  verdict: Forward | Drop | Rewrite(event') | Originate(extra events)
   RoomSession fan-out  ── deliver resulting events to the OTHER actors in R
```

### Components and their boundaries

- **`RoomSession`** (LoadBalancing) — per-room authority + membership + fan-out. Extends today's
  `Room` (which already holds actor numbers and, since this session, property storage). Owns the
  interceptor chain and the list of connected `PeerConnection`s in the room. *Does:* route an inbound
  event to interceptors, then fan the result out to other actors; track master-client designation.
  *Depends on:* `PeerConnection.RaiseEvent`, the codec.
- **`IEventInterceptor`** (LoadBalancing) — `Verdict Intercept(EventContext ctx)`. One clear job:
  inspect a decoded event and return Forward/Drop/Rewrite/Originate. The default chain is a single
  pass-through (pure relay). Authority interceptors (spawn, damage, movement) are added by event
  type. A throwing interceptor is caught and treated as **Forward** (never drops a player on a bug).
- **Codec decoders** (`BlackIce.Photon`) — extend existing GpBinary/message decoding to recognize:
  RPC method shortcuts (key 5) so RPC events are classifiable by method; the `DamagePacket` custom
  type (code 68, 41-byte layout, damage at offset 0); instantiation events (202, prefab/viewID/pos);
  and the position-serialize stream (201, absolute Vector3/Quaternion per viewID). Each is a small,
  independently oracle-testable unit.
- **`PlayerBot` + `IBotBehavior`** (LoadBalancing) — a server-side synthetic actor. `PlayerBot` owns
  the actor number + viewID and drives the fabricated-event lifecycle through the **same** fan-out
  path (the seam's Originate channel). `IBotBehavior` is the pluggable per-tick decision (movement
  first; combat later). `BotIdentityGenerator` produces free-form identity/appearance from pools.
- **`BotManager`** (host/LoadBalancing) — spawns/despawns bots into a `RoomSession`, runs the tick
  loop (reusing the listener's single-threaded maintenance cadence so room state needs no locking).

### Why one pipeline

"Relay now, authority incrementally, bots reuse the same path" all live in one seam. The relay is
Section-1 behavior (Forward verdict). Authority is the same pipeline returning Drop/Rewrite for
specific event types. A bot is the same pipeline's Originate channel with the server as event source.
This keeps a single, testable code path and makes tightening authority a threshold/config change, not
a re-architecture.

## Phased delivery (within this spec)

- **2a — Relay substrate + seam.** `RoomSession`, `IEventInterceptor` chain (pass-through default),
  fan-out to other actors for RaiseEvent/SetProperties/join/leave. Codec decoders for RPC-shortcut +
  202 + 201 + DamagePacket. **Outcome:** movement, spawns, damage, death replicate between two real
  clients. No authority yet.
- **2b — Mod-free authority (validate-and-log first).** Server claims master client. Spawn interceptor
  (originate/validate cached 202s; own the namespace). Damage interceptor (decode → validate → clamp/
  drop available, generous/off by default). Movement interceptor (speed/teleport gate → log; drop/
  clamp available). All conservative initially to avoid breaking legit play.
- **2c — Playerbot scaffolding.** `PlayerBot` lifecycle (join→props→instantiate→refresh→stream),
  `BotIdentityGenerator`, `BotManager` tick loop, one `WanderBehavior`. **Outcome:** a generated AI
  player visibly joins and moves in a real client.

## Data flow

1. Client A sends op 253 (RaiseEvent) / 252 (SetProperties); or A joins/leaves.
2. `PeerConnection` decodes the message (existing path) and hands the `RoomSession` a decoded event.
3. `RoomSession` builds an `EventContext` (room, sender actor, decoded event + classification) and
   runs the interceptor chain.
4. Verdict applied: Forward (relay as-is), Drop (swallow), Rewrite (relay the modified event),
   Originate (relay extra server-authored events, e.g. a corrected spawn or a bot action).
5. Fan-out: for each *other* connected actor in the room, `PeerConnection.RaiseEvent`. (Photon
   `TargetActors`/`ReceiverGroup` semantics on the inbound event are honored where the client set
   them.)
6. Bots: `BotManager` tick → `IBotBehavior` produces events → `RoomSession` Originate → same fan-out.

## Error handling

- A throwing interceptor is caught, logged at Error with the event classification, and treated as
  **Forward** — a bug in authority logic must never disconnect or freeze a player.
- Unknown/undecodable events fall through to **Forward** (pure relay) so unrecognized gameplay still
  works; the decoder logs at Debug what it couldn't classify.
- Fan-out send failures are logged per-peer (existing `Send` try/catch) and never abort the loop.
- All of this rides the diagnostic logging + global crash handlers added earlier this session.

## Testing strategy

- **Codec round-trips** against the real `Photon3Unity3D.dll` oracle for every new decoder: the
  `DamagePacket` 41-byte layout (damage at offset 0), the 201 position stream, the 202 instantiation
  payload, and RPC-shortcut classification. (Mirrors the existing oracle test pattern.)
- **Interceptor unit tests:** each verdict (Forward/Drop/Rewrite/Originate) for each authority
  interceptor, including the throw→Forward fallback.
- **Relay unit tests:** 2-actor `RoomSession` → A's event is delivered to B and not echoed to A;
  TargetActors/ReceiverGroup honored; a left actor stops receiving.
- **Bot unit tests:** `PlayerBot` emits the correct ordered lifecycle events; `BotIdentityGenerator`
  produces valid free-form identities; `WanderBehavior` yields position updates.
- **Live verification (gated behind the multiplayer moment):** two real clients see each other move/
  shoot/die; then a generated bot visibly joins and wanders in an unmodified client. Confirmed via the
  `--trace` server log + in-game observation, as with MOTD.

## Security / authority notes

- Mod-free authority governs **what other clients see**, not a cheater's own screen. This is the
  honest ceiling of the WoW-emulator approach on a PUN client and is acceptable for this phase
  (logging gives us the anticheat signal; tightening is incremental).
- Direct death veto and continuous own-view movement correction are **explicitly deferred** to a
  future client-mod-based authority phase; this spec does not require a client mod.
- The spoofable-SteamID gate (CLAUDE.md) is unchanged: bots are server-originated and never assert a
  Steam identity; the relay continues to trust no client-asserted privilege.

## Out of scope (future phases)

- A BepInEx client mod for hard authority (continuous position correction, direct death override).
- Combat AI for bots beyond the pluggable behavior seam (this phase ships movement/wander only).
- Persistence of bot state, matchmaking of bots across realms, or bot population management policy.
- Fragmentation/reliable-delta (206) rewriting beyond what the observed unreliable (201) stream needs.
