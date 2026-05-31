# Server Platform — Sub-project 2: Realms & Rulesets (Design)

**Project:** Black Ice independent server (open-source, GPLv3)
**Group:** Server Platform (2 of 3 — Persistence/Identity ✓ → **Realms** → In-game commands)
**Date:** 2026-05-30
**Status:** Approved (delegated) — pending spec review

---

## 1. Context

Phase 1 + SP1 are merged: the client connects, authenticates against SteamID-keyed accounts,
and joins a single hardcoded room (`[CUSTOM SERVER] Test Room`) advertised in the in-game
lobby browser. The browser renders each room via `ServerSlotUGUI`, which reads room custom
properties `PVP` (bool), `HackDifficultyIncrease` (int), and `Password` (string) — missing
ones make the slot throw (learned in Phase 1).

This sub-project turns that single room into many **DB-backed realms**, each with its own
ruleset (the game's native knobs), seeded from config and persisted.

## 2. Goals & Definition of Done

Multiple realms defined in config are seeded into the database; **all enabled, visible realms
appear in the in-game server browser** with correct PVP / hack-difficulty / password / max-
players; **joining any realm** puts the player in that realm's room with its ruleset applied
(room properties + Join event). Realms persist across restarts. Management is via config now;
in-game management is SP3.

Out of scope: in-game realm CRUD commands (SP3); server-*enforced* custom rules beyond the
native knobs (needs Phase 3 authority); cross-realm matchmaking.

## 3. Architecture

Realm *definitions* live in the database (`BlackIce.Server.Data`); live *occupancy* stays in
the in-memory `RoomRegistry`. The Master advertises realms; the Game applies a realm's rules
when a player enters its room.

### Realm entity (EF Core)

| Field | Type | Notes |
|---|---|---|
| `Name` | string (PK) | the room name the client joins by (also the browser label base) |
| `DisplayName` | string | shown name (defaults to Name) |
| `Pvp` | bool | → room prop `"PVP"` |
| `HackDifficultyIncrease` | int | → room prop `"HackDifficultyIncrease"` |
| `Password` | string | "" = open; non-empty advertised as locked (room prop `"Password"`) |
| `MaxPlayers` | int | → well-known room prop (255) |
| `IsVisible` | bool | listed in the browser when true |
| `IsEnabled` | bool | inactive realms are neither listed nor joinable |
| `ExtraJson` | string | JSON bag for future server-enforced rules (empty `{}` now; extensibility) |

### RealmService (Data)
- `IReadOnlyList<Realm> ListEnabled()` / `ListVisible()`
- `Realm? Get(string name)`
- `Realm Upsert(Realm realm)` / `bool Delete(string name)`
- `void SeedDefaults(IEnumerable<Realm> defaults)` — inserts the config realms only if the
  `Realms` table is empty (first run); afterwards the DB is source of truth.

### Config (`ServerConfig`)
- `List<RealmConfig> Realms` — starter realm definitions seeded on first run. Default config
  ships a small set: a default PvE realm, a PvP realm, and a hard-mode realm, so the browser
  isn't empty out of the box. (`TestRoomName` from Phase 1 is removed; the default PvE realm
  replaces it.)

## 4. Data flow

```
startup: ServerConfig.Realms → RealmService.SeedDefaults (only if table empty)
Master JoinLobby → GameList event (param 222) built from RealmService.ListVisible():
    each realm -> { 253 IsOpen, 254 IsVisible, 252 PlayerCount(live), 255 MaxPlayers,
                    "PVP", "HackDifficultyIncrease", "Password" }
player picks a realm in the browser → JoinRoom(name) → Master JoinGame → Game address
Game JoinGame/CreateGame(name):
    realm = RealmService.Get(name); if null/disabled → error
    if realm.Password != "" and join password param mismatches → error (basic check)
    room = RoomRegistry.GetOrCreate(name); actor assigned
    response room props + Join event carry the realm's ruleset
```

## 5. Components changed

- **`MasterServerHandler`**: `BuildGameListEvent()` iterates `RealmService.ListVisible()` instead
  of the single `_testRoomName`; player count comes from the live `RoomRegistry`. Takes a
  `RealmService`.
- **`GameServerHandler`**: `EnterRoom` looks up the realm, rejects unknown/disabled realms and
  password mismatches, and stamps the realm's ruleset into the room properties / Join event.
  Takes a `RealmService`.
- **`Host`**: builds `RealmService`, seeds from config, passes it to the handlers; drops the
  hardcoded `TestRoomName`/`registry.GetOrCreate(testRoomName)`.
- **`RoomRegistry`**: unchanged responsibility (live occupancy), now keyed by realm name.

## 6. Error handling
- Join to an unknown or disabled realm → operation response with a non-zero return code (client
  shows a join failure), never a crash.
- Password mismatch → non-zero return code.
- A realm with malformed `ExtraJson` is loaded with an empty bag (logged), not fatal.

## 7. Testing
- `RealmServiceTests` (in-memory SQLite): seed-only-when-empty; CRUD; list filters by
  enabled/visible.
- `MasterServerHandler` GameList lists multiple visible realms with correct props; hidden/
  disabled realms excluded.
- `GameServerHandler` `EnterRoom` applies the realm ruleset; unknown realm rejected; password
  mismatch rejected.
- Existing 79 tests stay green (the single-test-room test is replaced by realm-based tests).
- Live smoke: start server with default config realms; confirm the browser lists them and one
  is joinable (the autonomous run confirms the server advertises them; the in-game pick is the
  manual confirmation, as in Phase 1).

## 8. Out of scope (SP2)
- In-game realm management commands and the chat-RPC layer (SP3).
- Server-enforced custom gameplay rules (Phase 3 authority); `ExtraJson` is stored, not enforced.
- Per-realm world simulation/relay (Phase 2).
