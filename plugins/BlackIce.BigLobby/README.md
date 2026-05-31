# BlackIce.BigLobby

> **CLIENT-side** BepInEx mod (runs in the *Black Ice* game, not the server). One of the client mods in
> [`../README.md`](../README.md) — not part of the server plugin system.

> **Experimental.** Raises the client's room-size ceiling so a match can hold more than the stock ~8
> players on a BlackIce server. The game is only *verified* for ~8 per match; larger sizes are unproven —
> see **Caveats**.

## Why this exists

The **server already supports large rooms** — a room structurally holds thousands of actors, and a
realm's `MaxPlayers` is operator-set (`0` = unlimited; see [`../../docs/large-servers.md`](../../docs/large-servers.md)).
The ~8 limit is a **client assumption**: the game asks Photon to create/join rooms with a small
`MaxPlayers`, and once a room's declared capacity is reached **Photon itself refuses further joins** — so
the cap bites before the server ever gets a say.

This mod overrides that declared capacity at the **public PUN API boundary**, so the client stops
self-limiting and a big realm can actually fill.

## What it patches

Public PUN surface only — no game internals:

- **`RoomOptions` construction** — when the game leaves `MaxPlayers` at its small default (or `0`), raise it
  to the configured ceiling. It only ever **enlarges**; a cap the game deliberately set higher is left alone.

That single seam covers both hosting and the capacity the client will accept, regardless of exactly how the
game creates its rooms.

## Config

`BepInEx/config/blackice.biglobby.cfg`:

| Key | Default | Meaning |
|---|---|---|
| `Lobby.MaxPlayers` | `32` | Room capacity to request. **Clamped to 1–255** (PUN's `MaxPlayers` is a byte). |

The **server's realm `MaxPlayers` must be raised to match** — both sides have to agree. Set it via config
(`docs/configuration.md`) or the `realm` console commands. (The server clamps its advertised lobby count to
255 too, since the browser slot is byte-typed.)

## Caveats — read before using

- **Not a stability guarantee.** This overrides a *client design assumption*; it does not make the game's
  rendering, HUD, or netcode correct at large sizes. Those were built for small matches and are
  **unverified above 8**. Treat big matches as experimental (it's on the live-verification roadmap).
- **Hard ceiling 255** — the Photon wire format carries `MaxPlayers` as a byte.
- **No-op alone** — without the server's realm cap raised to match, nothing changes.

## Build / deploy

```bash
dotnet build BlackIce.sln          # builds the client mods (and the server + tools)
```

Targets `netstandard2.0` (BepInEx/Mono) and auto-deploys into the game's `BepInEx/plugins` on build. It
references the game's BepInEx/Unity/Photon DLLs, which are **not** committed (game-derived; see `NOTICE`).
