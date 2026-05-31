# Large servers & the player-count limits

A common question: *"Can I run a server with far more than the 8-player limit?"* The answer has three
layers, because "8 players" means different things in different places.

## TL;DR

| Scope | Limit | Set by |
|---|---|---|
| **Total players on the server** | effectively unbounded (hardware) | — run many realms/rooms |
| **Players who can JOIN one realm** | the realm's `MaxPlayers` (config; `0` = unlimited), up to **9,999** structurally | **our server** enforces it — unmodified clients included |
| **Players the stock client RENDERS in one match** | **~8** (design assumption, unverified above) — an in-match concern, **not** a join block | the **game client**, not us |
| **Count shown in the lobby browser** | **0–255** per slot | the Photon wire format (byte-typed) |

## Total players: not limited to 8 — that's the whole point

The server is built on the OpenRA / TrinityCore model: **one server hosts many concurrent games**.
Nothing caps the *server* at 8.

- `MaxPlayers` is **per-realm** (see `docs/configuration.md` / the `realm` console commands), default 8 but
  freely configurable. **`MaxPlayers <= 0` means unlimited** — the capacity check is skipped
  (`GameServerHandler` rejects a join with `rc=-6` only when a positive cap is reached).
- Run as many realms as you like; total concurrent players is bounded only by your hardware.

## One match (one room) beyond 8

- **Server side:** a single room structurally supports up to **9,999** real players. Real actor numbers
  must stay below the bot range (`BotManager.BotActorBase = 10000`) because each actor owns a viewID block
  of `actor * 1000`; `RoomRegistry.AddActor` throws if that space is exhausted. So the server itself is not
  the 8-limit.
- **Client side:** the **8 is the game's own design point**, not a join gate on our server. **Capacity is
  enforced entirely server-side** — `GameServerHandler.EnterRoom` admits a join only against the realm's
  configured `MaxPlayers` (`rc=-6 "Room full"` when reached); it never consults anything the client
  requested. So an **unmodified client CAN join a realm configured for >8** — set `MaxPlayers = 32` and the
  server admits up to 32 stock clients. What's *unverified* is whether the game's **rendering / HUD /
  netcode hold up** once >8 avatars are actually in one match (it was built around ~8 with master-client
  authority). That's an in-match client concern, not a connection block.

**An unmodified client joins a >8 realm fine.** The optional client mod
[`BlackIce.BigLobby`](../plugins/BlackIce.BigLobby/README.md) is a **smoothing/verification** aid, not a
requirement: the client that *creates* a room still calls PUN with the game's small baked-in
`RoomOptions.MaxPlayers` (~8), which the server ignores for the gate but which the creating client's own
local PUN/HUD still believes — BigLobby raises that so the creating client's view matches the big realm.
See its README for exactly what it changes and the **caveats** (overriding a client assumption is not a
guarantee the game is stable at large sizes — that's a live-verification item).

## The lobby browser count is a single byte

PUN's `RoomInfo` exposes `PlayerCount` and `MaxPlayers` as a **byte**, so the lobby server browser can only
represent **0–255** per slot. The server therefore **clamps** these to 255 (a raw cast would *wrap* —
500 → 244, 256 → 0 — and corrupt the slot). A realm with a cap above 255, or an unlimited realm, advertises
a saturated **255** in the browser. The room can still *hold* more than 255 on the server; only the
advertised number saturates. (The server console's `rooms` command shows the true counts, unclamped.)

## What scales how

- **Want more players overall?** Add realms / raise per-realm caps. Already supported, no mod.
- **Want a single match with >8 players?** Raise the server realm's `MaxPlayers`. Unmodified clients will
  **join** it (the server is the gate). Optionally install `BlackIce.BigLobby` on clients so the
  room-creator's local view matches the big cap — and treat the game's in-match stability above 8 as
  **unverified** until tested against the real client.
