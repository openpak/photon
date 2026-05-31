# Phase 3 — Server Authority / Anti-cheat — Design Spec

**Status:** Approved design (2026-05-31). Source directive: `docs/design/phase3-authority-directive.md`.
**Scope decision:** This spec covers *all* of Phase 3, anchored on a server game-state model, but
decomposed into three independently buildable sub-phases (3a / 3b / 3c) — each gets its own
implementation plan. Mirrors the Phase 2 → 2a/2b/2c structure.

---

## 1. Problem & goal

Black Ice is client-authoritative (P2P-via-relay). After Phase 2 the server *relays* in-room events
and *detects* a couple of anomalies (log-only `MovementValidationInterceptor`,
`DamageValidationInterceptor`) but trusts every gameplay value clients send — position, damage, kills,
loot, XP. RPC handlers do zero authority checks (`docs/protocol/03-rpc-catalog.md`: all 85 RPCs are
client-trusted receivers).

Phase 3 turns the server from a *trusting relay* into an *authority*, per the owner directive's
**hybrid** posture:

- **Movement / position / input:** accept as unverified input, validate server-side (max-speed,
  teleport, out-of-bounds, rate/sanity), **reject or snap-correct** — do *not* fully discard (would
  break client prediction and feel laggy).
- **Consequential outcomes (damage, kills, loot, XP, item grants):** **zero-trust** — the server
  validates/recomputes them from authoritative state instead of trusting client packets.
- **Hit registration:** **lag-compensation / rollback** — rewind to the shooter's acknowledged world
  view so legitimate high-latency hits count without trusting the client's damage claim.

### Key design decisions (resolved during brainstorming)

1. **Game-state model = hybrid: shadow-state now, full recompute later.** The server maintains a
   *shadow* of room state (observed from relayed events) and validates each claimed delta for
   plausibility/consistency. A pluggable rule-evaluator seam (`IOutcomeRule`) lets specific
   high-value outcomes be upgraded to true clean-room recompute later **without rearchitecting**.
   This is clean-room-safe (no copying decompiled formulas), ships value fast, and still satisfies
   the directive's "recompute from validated state."
2. **Single-threaded authority.** Authority runs on the existing single listener thread (today's
   invariant). Add a `Debug.Assert` guarding it; make only the cross-thread counters concurrent.
   Closes the latent race documented in `.remember/chaos-findings-2026-05-31.md`.
3. **Fail-open everywhere.** An authority bug must never punish a legitimate player — the worst it may
   do is *miss* a cheat.
4. **Default strictness = `Observe`.** Shipping 3a changes nothing in production until a realm opts
   in. Thresholds are config, not constants, and get tuned at `Warn` before any realm goes `Enforce`.

---

## 2. Architecture

Everything hangs off the **existing interceptor seam** — no new plumbing for the relay path:

- `IEventInterceptor.Intercept(EventContext) → RelayVerdict`
- `InterceptorChain.Run(ctx)` runs interceptors in order, first non-`Forward` verdict wins, a
  throwing interceptor is caught → treated as `Forward`.
- `RelayVerdict` already supports `Forward / Drop / Rewrite / Originate`.
- Wired: `RelayVerdict → interceptor → RoomSession → registry → GameServerHandler`.
- Per-realm config available via `Realm.ExtraJson`.

Phase 3 makes the interceptors **stateful against an authoritative model** and lets them act on
their verdicts.

### New components (all per-room, all on the listener thread)

| Component | Responsibility | Depends on |
|---|---|---|
| `RoomWorldState` | Authoritative *shadow* of the room: entities keyed by `viewId` (kind, owner actor, HP, position, inventory deltas, XP, alive/dead). Updated from relayed events. | `EventContext`, packet parsers |
| `IOutcomeRule` (+ default impl) | Pluggable evaluator: given a claimed delta + `RoomWorldState`, returns `Valid` / `Reject` / `Correct`. Default = plausibility/consistency checks; future = full recompute for specific RPCs. **The hybrid hook.** | `RoomWorldState` |
| `AuthorityPolicy` | Per-realm strictness (`Observe → Warn → Enforce → Strict`) read from `Realm.ExtraJson`. Maps a rule result + strictness → a `RelayVerdict`. | `Realm` |
| `ViolationTracker` | Per-(room, actor) flag accumulator + escalation (log → suppress → kick). **Thread-safety lives here** (subsumes today's `FlaggedCount`). | connection registry (for kick) |
| `WorldSnapshotHistory` | Ring buffer of timestamped entity positions for lag-comp rewind. **3c only.** | `RoomWorldState` |

The two existing validators are rebuilt as rule-driven interceptors against `RoomWorldState`:
movement keeps its kinematics check and gains OOB / bounds / rate + snap-correct (`Rewrite`);
damage becomes an outcome interceptor driven by `IOutcomeRule`.

### Sub-phase sequencing (one spec, three buildable plans)

- **3a — Enforcement & policy.** Flip movement/damage detectors to *act* (`Drop` / `Rewrite`);
  `AuthorityPolicy` + per-realm strictness; `ViolationTracker` + thread-safety hardening.
  *No full world model yet — only the per-view kinematics the movement interceptor already tracks.*
- **3b — Shadow world-state & zero-trust outcomes.** `RoomWorldState` + `IOutcomeRule`
  delta-validation for damage / HP / kills / loot / XP.
- **3c — Lag-comp / rollback hit registration.** `WorldSnapshotHistory` + shot rewind.

**Rejected alternative (threading):** make `RoomWorldState`/`ViolationTracker` fully concurrent now —
premature; adds lock contention to the hot relay path for a multi-threaded relay we don't have.
Documented as the trigger to revisit: *if* a multi-threaded relay path is introduced, make these
concurrent (matching the `EnetPeer` `_seqLock` precedent, commit `8d49495`).

---

## 3. Data flow

Every in-room event already passes through `InterceptorChain.Run(ctx)` on the listener thread. Phase 3
makes that pass do three things in fixed order: **(1) update shadow state from trusted facts,
(2) validate the claimed change, (3) emit a verdict + record violations.**

### Movement (event 201, `NetworkSyncPosition`)

```
inbound 201 → MovementInterceptor
  read prev (RoomWorldState[room,viewId])
  compute speed; check teleport / OOB / rate vs AuthorityPolicy thresholds
  ├─ within bounds → update shadow pos → Forward
  └─ violation → ViolationTracker.flag(actor)
       Observe/Warn  → update shadow + Forward (log)
       Enforce/Strict → Rewrite(corrective 201 = last good pos)  // snap-correct, don't discard
                        shadow pos stays at last good
```

**Invariant:** shadow position only advances to positions we *accepted* — a rejected teleport cannot
poison the next frame's speed calculation.

### Outcomes (event 200, damage / kill / loot / XP RPCs)

```
inbound 200 → OutcomeInterceptor
  parse claim (source view, target view, delta)
  IOutcomeRule.evaluate(claim, RoomWorldState):
     - source & target exist and alive?
     - delta within bounds? HP monotonic (no heal w/o heal event)?
     - item/XP grant has a legitimate cause in shadow state?
  ├─ Valid   → apply delta to shadow (target HP -= dmg, etc.) → Forward
  ├─ Reject  → ViolationTracker.flag → Enforce: Drop ; Observe: Forward + log
  └─ Correct → apply corrected delta → Rewrite(corrected event)
```

**Invariant — apply-after-validate:** `RoomWorldState` mutates only on accepted/corrected events,
never on rejected ones — so the shadow can never be driven out of sync by a cheat it just rejected.

### Hit-reg with rollback (3c)

A damage claim naming a *moving* target first rewinds `WorldSnapshotHistory` to the shooter's
acknowledged tick, re-tests the geometry there, then runs the normal outcome rule against that
rewound state.

### State lifecycle

`RoomWorldState` + `WorldSnapshotHistory` are created with the `RoomSession`, populated on join/spawn
events (reusing the Phase 2 spawn cache), entries dropped on despawn/leave, the whole thing GC'd when
the room closes.

---

## 4. Error handling, strictness & escalation

**Fail-open is the governing principle** (extends the existing `InterceptorChain` catch → `Forward`):

- Parse failure, unknown event, or **entity not yet in shadow state** → `Forward` (no opinion). The
  shadow is best-effort; a missing entity is "can't judge," never "cheat."
- A rule that throws → caught, logged, `Forward`.
- Net: the worst an authority defect can do is *miss* a cheat, never drop a real action.

### Strictness levels (per-realm, `Realm.ExtraJson` → `AuthorityPolicy`)

| Level | Movement violation | Outcome violation | Use |
|---|---|---|---|
| `Observe` | log only, forward | log only, forward | today's behavior; **default**; safe rollout |
| `Warn` | log + count, forward | log + count, forward | tuning thresholds against live play |
| `Enforce` | snap-correct (`Rewrite`) | `Drop` | the real anti-cheat posture |
| `Strict` | `Enforce` + escalation | `Enforce` + escalation | hardcore realms |

### Escalation (`ViolationTracker`, `Strict` only)

Per-(room, actor) rolling flag count. Thresholds drive **log → temporary suppression → kick**
(disconnect the peer via the existing connection registry). Counts **decay** over time so a laggy
spike doesn't accumulate into a ban. **No persistent bans in Phase 3** — that's Phase 4; here
escalation is session-scoped only.

### Snap-correction caveat

A corrective `Rewrite` (201 → last-good position) briefly fights client-side prediction — the cheater
rubber-bands. Intended for cheats; the risk is a *false positive* rubber-banding a legit laggy player,
which is exactly why thresholds get tuned at `Warn` before any realm goes `Enforce`.

### Thread-safety

`ViolationTracker` counters use `Interlocked`; `RoomWorldState` stays listener-thread-confined behind
a `Debug.Assert` guarding the invariant. This closes the latent race documented in the chaos findings
and lets us tighten `ConnectionStormTests` to exact-equality.

---

## 5. Testing strategy

Project discipline carries in: xUnit, the **real `Photon3Unity3D.dll` as interop oracle**, and the
chaos suite as regression backstop. Coverage gate per sub-phase: green suite + oracle round-trip
before merge.

### 3a — Enforcement & policy
- `AuthorityPolicy` mapping: `(strictness × violation-type) → expected RelayAction` as a `[Theory]`.
- `MovementInterceptor`: in-bounds → `Forward`; teleport/OOB/rate under `Enforce` → `Rewrite`
  carrying last-good position; **shadow position does not advance on a rejected frame**.
- `ViolationTracker`: concurrency test (parallel `flag()` → exact count via `Interlocked`); decay;
  escalation threshold → kick signal. Then **tighten `ConnectionStormTests` from tolerant to
  exact-equality**.
- **Interop oracle:** every corrective `Rewrite` (201) round-trips through the real DLL (a corrective
  event the client can't decode is worse than forwarding).

### 3b — Shadow state & outcomes
- `RoomWorldState`: join/spawn populates, despawn/leave evicts, room-close clears (reuse Phase 2
  spawn-cache fixtures).
- `IOutcomeRule` default: HP monotonicity, damage bounds, source/target existence+alive,
  item/XP-from-nowhere — each a focused `[Fact]`.
- **Fail-open tests:** unknown entity, parse failure, throwing rule → `Forward` (never `Drop`).
- Apply-after-validate: a rejected damage claim leaves target HP untouched.

### 3c — Rollback
- `WorldSnapshotHistory` rewind: a shot valid at the shooter's acked tick but not at present-time →
  accepted; geometry tests at boundary ticks.

### Cross-cutting
Re-run the full chaos/stress suite under each sub-phase (esp. 3000-bot + fuzz) — authority adds
per-event state, so hostile-input and high-load guarantees must still hold.

---

## 6. Out of scope (Phase 3)

- Persistent bans / cross-session reputation (Phase 4).
- Full clean-room reimplementation of game damage/loot/XP formulas (the `IOutcomeRule` seam exists so
  individual outcomes *can* be upgraded later, but Phase 3 ships the shadow/plausibility evaluator).
- Multi-threaded relay and concurrent world-state (documented trigger to revisit; not built now).
- Networked admin / privileged actions (still blocked on the Steam game-server ticket gate — see
  `CLAUDE.md` SECURITY GATE).
