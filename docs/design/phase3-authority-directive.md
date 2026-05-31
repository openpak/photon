# Phase 3 (Server Authority / Anti-cheat) — Design Directive

Captured 2026-05-30 from project owner. Informs the future Phase 3 spec.

**Problem:** Black Ice is client-authoritative (P2P-via-relay). The server currently trusts
all client-sent gameplay values — position (NetworkSyncPosition x/y/z), DamagePacket, loot,
XP — none are validated (RPC handlers do zero authority checks; see docs/protocol/03-rpc-catalog.md).

**Directive (hybrid authority, not all-or-nothing):**
- **Movement / position / client input:** accept as *unverified input*, but **validate server-side**
  — max-speed, teleport-distance, out-of-bounds, and rate/sanity checks. Reject or snap-correct
  violations. Do NOT fully discard (would break the client's prediction and feel laggy).
- **Consequential outcomes (damage, kills, loot drops, XP, item grants):** **zero trust** — the
  server recomputes them from validated state instead of trusting client packets like DamagePacket.
- **Hit registration:** use **lag compensation / rollback** — rewind to the shooter's acknowledged
  world view to validate a shot, so legitimate high-latency hits count without trusting the client's
  damage claim.

**Net:** limited, validated trust for movement; full server authority for outcomes; rollback for hits.
This is the concrete anti-cheat posture Phase 3 implements (likely needs the OfflineMode sim as the
authoritative reference + per-realm enforcement strictness via Realm.ExtraJson).
