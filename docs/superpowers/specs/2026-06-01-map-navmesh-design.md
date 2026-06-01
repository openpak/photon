# Map NavMesh Extraction & Bot Navigation — Design Spec

**Status:** Design (2026-06-01), branch `feat/map-navmesh`.
**Goal:** Give the server real walkable-surface knowledge so playerbots path along the map instead of
clipping through geometry / floating / dying to hazards — while keeping the clean-room rule intact.

## Why this exists (the constraint that forces the architecture)

Confirmed from a live capture: the master client relays only **dynamic gameplay entities** (loot, enemies,
powerups, barrels, players) over Photon — **never terrain, buildings, or navmesh.** Level geometry is
static client-side asset data each client loads locally. So the server is structurally blind to the map,
which is exactly why bots clip through walls and need the player as their only safe-ground anchor.

The only source of map geometry is the **game's own asset files**. That output is **game-derived material**
— it must NOT be committed (clean-room; `.gitignore` already blocks `asset-dumps/`, `Black Ice_Data/`).
This dictates a three-part shape, none of which leaks game data into the repo:

```
[offline extractor tool]  →  [gitignored navmesh artifact]  →  [server loads it at runtime]
 (committed: original code)   (local only: maps/*.navmesh)      (falls back to player-anchor if absent)
```

## Asset format (surveyed, not guessed)

- Engine: **Unity 2020.3.49f1**, standard serialized scene files: `Black Ice_Data/levelN` (+ `.resS` mesh
  blobs). Playable arenas are the large ones (`level10`–`level18`, 10–32 MB).
- Each arena scene contains a baked **NavMeshData** object (walkable polygon mesh), plus `NavmeshObstacle`
  and `MeshCollider` components (confirmed via type-name strings: 57–222 navmesh markers per arena).
- **NavMeshData is the target.** It's the engine's pre-tessellated walkable surface — smaller and directly
  usable for pathing, vs. reconstructing walkability from raw building meshes.

## Scope (navmesh first, collision later — per owner)

**Phase A — NavMesh pathing (this spec's build target):**
1. Extract the baked NavMeshData (walkable triangles + their adjacency) from each arena scene.
2. Emit a compact, game-agnostic `*.navmesh` artifact (our own format — vertices, triangles, links; NO
   Unity/game types, so the *format* is original even though it describes game geometry → the artifact
   stays gitignored regardless).
3. Server loads the artifact for the active map; bots path on it (nearest-point-on-mesh + A* over triangle
   adjacency + funnel smoothing). Replaces "guess a coordinate" with "walk the real surface."

**Phase B — Full collision geometry (spec'd, deferred):** extract MeshCollider geometry + a spatial index
for true raycast/segment-vs-world queries (line-of-sight, projectile checks). Larger and heavier per tick;
build only once navmesh pathing proves out.

## Components

### 1. Extractor tool — `tools/MapExtractor/` (committed, original code)
- A small **.NET console tool** (keeps the toolchain one language; no Python dep for contributors). Reads a
  Unity 2020.3 serialized scene via **AssetsTools.NET** (MIT, NuGet — a clean-room Unity serialized-asset
  reader; we write original code against its API, we don't ship game data).
  - *Alternative considered:* UnityPy (Python). Rejected to avoid adding a second toolchain; revisit only
    if AssetsTools.NET can't read NavMeshData cleanly.
- Input: a game scene file path (operator points it at their local `Black Ice_Data/levelN`).
- Output: `maps/<name>.navmesh` (gitignored) in our format.
- Usage: `dotnet run --project tools/MapExtractor -- <scene-file> <out.navmesh> [--map-name X]`.
- **Clean-room guard:** the tool refuses to write its output anywhere git-tracked; documents that the
  artifact is local-only; never copies raw game bytes into the repo tree.

### 2. NavMesh format + loader — `server/BlackIce.Server.Core/Navigation/` (committed)
- `NavMesh` — vertices, triangles, per-triangle neighbor links; built from the artifact. Pure data + queries:
  `NearestPointOnMesh(x,z)`, `FindPath(from, to)` (A* over triangles + funnel), `Sample(y)` ground height.
- `NavMeshFile` — read/write our binary format (versioned header, float verts, int tris/links). Original
  format → unit-testable with a hand-built tiny mesh, no game asset needed.
- This lives in Core (not the extractor) so the server depends only on *our* format, never on AssetsTools.

### 3. Server wiring — load + serve to bots
- A `NavMeshRegistry` (DI singleton, mirrors `RoomWorldStateRegistry`): loads `maps/<realm-map>.navmesh`
  at startup if present; null if absent.
- `HunterBehavior` gains an optional `NavMesh`: when present, movement snaps to `NearestPointOnMesh` and
  approaches via `FindPath` waypoints (no clip, no float, no lava); when absent, **exactly today's
  player-anchor behavior** (zero regression for operators without an extracted map).
- Map→realm association: config (`Realm.ExtraJson` `{ "navmesh": "level13" }`) or a console command;
  default none → fallback behavior.

## Governing principles
- **Clean-room absolute:** extractor is original code; extracted navmesh is gitignored; repo never contains
  game geometry. Add `maps/` to `.gitignore`.
- **Graceful absence:** no artifact → server behaves exactly as it does today. The feature is purely
  additive; a contributor without the game still builds, tests, and runs everything.
- **Our format, our tests:** `NavMesh`/`NavMeshFile` are testable with synthetic meshes — full coverage
  without shipping a single game byte.
- **Spec→plan→build:** this spec → a plan (extractor, format/loader, pathing, wiring as ordered tasks) →
  TDD build.

## Out of scope
- Phase B full collision geometry (spec'd above, deferred).
- Dynamic NavmeshObstacle carving at runtime (the baked mesh is static; obstacles are a later refinement).
- Shipping or committing any extracted map data, ever.
