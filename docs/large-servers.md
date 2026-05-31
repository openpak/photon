# Large servers & the player-count limits

A common question: *"Can I run a server with far more than the 8-player limit?"* The answer has three
layers, because "8 players" means different things in different places.

## TL;DR

| Scope | Limit | Set by |
|---|---|---|
| **Total players on the server** | effectively unbounded (hardware) | — run many realms/rooms |
| **Players in one realm (room)** | up to **9,999** structurally | the server's actor/viewID scheme |
| **Players the stock client renders in one match** | **~8** (design assumption, unverified above) | the **game client**, not us |
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
- **Client side:** the **8 is the game's own design point**, not ours. Black Ice was built around small
  Photon rooms with master-client authority; its rendering, netcode, and UI assumptions for a *single
  match* are unverified above 8 and are the most likely thing to break in a big room. We can't change that
  server-side — it lives in the client.

**To actually field a >8 match you need the client-side mod** [`BlackIce.BigLobby`](../plugins/BlackIce.BigLobby/README.md),
which raises the client's room-size ceiling (and the lobby `MaxPlayers` it will accept). See its README for
exactly what it changes and the **caveats** (it's a client assumption being overridden, not a guarantee the
game is stable at large sizes — that's a live-verification item).

## The lobby browser count is a single byte

PUN's `RoomInfo` exposes `PlayerCount` and `MaxPlayers` as a **byte**, so the lobby server browser can only
represent **0–255** per slot. The server therefore **clamps** these to 255 (a raw cast would *wrap* —
500 → 244, 256 → 0 — and corrupt the slot). A realm with a cap above 255, or an unlimited realm, advertises
a saturated **255** in the browser. The room can still *hold* more than 255 on the server; only the
advertised number saturates. (The server console's `rooms` command shows the true counts, unclamped.)

## What scales how

- **Want more players overall?** Add realms / raise per-realm caps. Already supported, no mod.
- **Want a single huge match?** Raise the server cap *and* install `BlackIce.BigLobby` on each client — and
  treat client stability above 8 as unverified until tested against the real game.
